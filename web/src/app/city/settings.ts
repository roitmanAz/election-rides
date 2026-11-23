import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../core/api';
import { errorMessage } from '../core/auth.service';

interface Settings {
  name: string;
  hqPhone: string | null;
  hoursFrom: string | null;
  hoursTo: string | null;
  electionDay: string | null;
  electionType: 'Knesset' | 'Municipal' | null;
  inActiveOperation: boolean;
}

/** Israeli phone rules (spec 5.1): 05/07 with 10 digits, 02/03/04/08/09 with 9. */
export function isValidPhone(v: string): boolean {
  const d = v.replace(/\D/g, '');
  return (d.length === 10 && /^0[57]/.test(d)) || (d.length === 9 && /^0[23489]/.test(d));
}

/** City settings (spec 3.1): HQ phone (required), election day, opening hours, election type. */
@Component({
  selector: 'app-city-settings',
  imports: [FormsModule],
  template: `
    <section class="card max-w-2xl overflow-hidden">
      <div class="border-b border-line px-5 py-4"><h1 class="text-xl font-semibold">הגדרות העיר</h1></div>
      @if (s(); as st) {
        <form class="grid gap-4 p-5 sm:grid-cols-2" (ngSubmit)="save()" novalidate>
          <div class="field sm:col-span-2">
            <label class="label" for="hq">מה הטלפון של המטה?<span class="req">*</span></label>
            <input id="hq" name="hq" class="input max-w-xs" type="tel" inputmode="numeric" dir="ltr" style="text-align: right"
              [(ngModel)]="phone" [class.invalid]="phoneErr()" (blur)="checkPhone()" />
            <div class="error-text">{{ phoneErr() }}</div>
            <p class="text-sm text-muted">מוצג לנהגים בכפתור "התקשר למטה".</p>
          </div>
          <div class="field">
            <label class="label" for="day">מתי יום הבחירות בעיר?</label>
            <input id="day" name="day" class="input" type="date" [(ngModel)]="day" />
            <p class="text-sm text-muted">
              @if (st.inActiveOperation) { סוג הבחירות: {{ st.electionType === 'Knesset' ? 'כנסת' : 'רשויות מקומיות' }}. }
              @else { העיר עוד לא משתתפת במבצע בחירות פעיל. }
            </p>
          </div>
          <div class="field">
            <span class="label">באילו שעות המטה פעיל?</span>
            <div class="flex items-center gap-2">
              <input name="from" class="input" type="time" aria-label="משעה" [(ngModel)]="from" />
              <span class="text-muted">עד</span>
              <input name="to" class="input" type="time" aria-label="עד שעה" [(ngModel)]="to" [class.invalid]="hoursErr()" />
            </div>
            <div class="error-text">{{ hoursErr() }}</div>
          </div>
          @if (error()) { <p class="notice bad sm:col-span-2" role="alert">{{ error() }}</p> }
          @if (saved()) { <p class="notice sm:col-span-2" role="status">ההגדרות נשמרו</p> }
          <div class="sm:col-span-2"><button class="btn-primary" type="submit" [disabled]="busy()">שמירה</button></div>
        </form>
      }
    </section>
  `,
})
export class SettingsPage implements OnInit {
  private readonly api = inject(Api);
  protected readonly s = signal<Settings | null>(null);
  protected readonly phoneErr = signal('');
  protected readonly hoursErr = signal('');
  protected readonly error = signal('');
  protected readonly saved = signal(false);
  protected readonly busy = signal(false);
  protected phone = '';
  protected day = '';
  protected from = '';
  protected to = '';

  async ngOnInit() {
    const s = await this.api.get<Settings>('/api/city/settings');
    this.s.set(s);
    this.phone = s.hqPhone ?? '';
    this.day = s.electionDay ?? '';
    this.from = s.hoursFrom?.slice(0, 5) ?? '';
    this.to = s.hoursTo?.slice(0, 5) ?? '';
  }

  protected checkPhone() {
    this.phoneErr.set(
      !this.phone.trim() ? 'צריך למלא את טלפון המטה'
        : isValidPhone(this.phone) ? ''
        : 'המספר לא תקין. נייד: 10 ספרות (05 או 07). קווי: 9 ספרות (02, 03, 04, 08, 09)',
    );
    return !this.phoneErr();
  }

  protected async save() {
    const hoursOk = !this.from || !this.to || this.to > this.from;
    this.hoursErr.set(hoursOk ? '' : 'שעת הסיום צריכה להיות אחרי שעת הפתיחה');
    if (![this.checkPhone(), hoursOk].every(Boolean)) return;
    this.busy.set(true);
    this.error.set('');
    this.saved.set(false);
    try {
      await this.api.put('/api/city/settings', {
        hqPhone: this.phone, hoursFrom: this.from || null, hoursTo: this.to || null, electionDay: this.day || null,
      });
      this.saved.set(true);
    } catch (e) {
      this.error.set(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}
