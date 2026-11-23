import { Component, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Api } from '../core/api';
import { errorMessage } from '../core/auth.service';
import { Combo, ComboItem } from '../shared/combo';
import { Dialog } from '../shared/dialog';
import { Icon } from '../shared/icon';
import { Shell, ShellTab } from '../shared/shell';

/** System admin screen (spec 3.2). Only "cities" for now; the other screens come in stage 8. */
@Component({
  selector: 'app-system-admin',
  imports: [Shell],
  template: `<app-shell [tabs]="tabs" subtitle="מנהל מערכת" menuLabel="תפריט מנהל המערכת" />`,
})
export class SystemAdmin {
  protected readonly tabs: ShellTab[] = [{ path: 'cities', label: 'ערים', icon: 'city' }];
}

interface CityRow {
  id: string;
  name: string;
  isActive: boolean;
  admins: { fullName: string; email: string; lastLoginAt: string | null; invited: boolean }[];
  setup: Record<'clusters' | 'streets' | 'communities' | 'voterBase' | 'users' | 'hqPhone', boolean>;
}

const STEPS: { key: keyof CityRow['setup']; label: string }[] = [
  { key: 'clusters', label: 'אשכולות' },
  { key: 'streets', label: 'רחובות' },
  { key: 'communities', label: 'קהילות' },
  { key: 'voterBase', label: 'בייס' },
  { key: 'users', label: 'משתמשים' },
  { key: 'hqPhone', label: 'טלפון המטה' },
];
const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

/** Cities (spec 3.2): setup progress out of 6 steps, city admins, and a new city with its first admin. */
@Component({
  selector: 'app-cities',
  imports: [FormsModule, Dialog, Icon, Combo, DatePipe],
  template: `
    <section class="card overflow-hidden">
      <div class="flex flex-wrap items-center gap-3 border-b border-line px-5 py-4">
        <h1 class="text-xl font-semibold">ערים</h1>
        <button type="button" class="btn-primary ms-auto" (click)="openNew()"><app-icon name="plus" [size]="18" /> עיר חדשה</button>
      </div>
      @if (notice()) { <p class="notice mx-5 mt-4" role="status">{{ notice() }}</p> }
      <div class="grid gap-3 p-3 sm:p-5">
        @for (c of cities(); track c.id) {
          <article class="grid gap-3 rounded-2xl bg-bg p-4 md:grid-cols-[minmax(0,1fr)_minmax(0,1.4fr)]">
            <div>
              <h2 class="text-lg font-semibold">{{ c.name }} @if (!c.isActive) { <span class="text-sm font-normal text-muted">(מושבתת)</span> }</h2>
              @for (a of c.admins; track a.email) {
                <p class="text-sm">{{ a.fullName }} <span class="text-muted" dir="ltr">{{ a.email }}</span>
                  @if (a.invited) { <span class="text-warn">· הוזמן, עוד לא נכנס</span> }
                  @else if (a.lastLoginAt) { <span class="text-muted">· נכנס {{ a.lastLoginAt | date: 'dd.MM.yy HH:mm' }}</span> }
                </p>
              } @empty {
                <p class="inline-flex items-center gap-1 text-sm font-medium text-bad"><app-icon name="warn" [size]="16" /> אין מנהל עיר</p>
              }
            </div>
            <div>
              <p class="mb-1.5 text-sm text-muted">הקמה: {{ done(c) }} מתוך 6 שלבים</p>
              <ul class="flex flex-wrap gap-1.5">
                @for (s of steps; track s.key) {
                  <li class="inline-flex items-center gap-1 rounded-full px-2.5 py-0.5 text-sm"
                    [class]="c.setup[s.key] ? 'bg-action-soft text-ink' : 'bg-surface text-muted'">
                    @if (c.setup[s.key]) { <app-icon name="check" [size]="14" /> }{{ s.label }}
                  </li>
                }
              </ul>
            </div>
          </article>
        } @empty {
          <p class="px-3 py-6 text-center text-muted">עוד אין ערים. "עיר חדשה" פותחת עיר וממנה לה מנהל עיר.</p>
        }
      </div>
    </section>

    <app-dialog [open]="newOpen()" title="עיר חדשה" [width]="520" (closed)="newOpen.set(false)">
      <form class="grid gap-3" (ngSubmit)="create()" novalidate>
        <div class="field">
          <label class="label" for="loc">איזו עיר?<span class="req">*</span></label>
          <app-combo inputId="loc" [items]="localities()" [(value)]="localityCode" [invalid]="!!locErr()"
            placeholder="מקלידים לפחות 2 אותיות" emptyText="לא נמצא ברשימת היישובים" (typed)="searchLocalities($event)" (left)="checkLoc()" />
          <div class="error-text">{{ locErr() }}</div>
        </div>
        <div class="field">
          <label class="label" for="adm-name">מה השם של מנהל העיר?<span class="req">*</span></label>
          <input id="adm-name" name="adminName" class="input" [(ngModel)]="adminName" [class.invalid]="nameErr()" (blur)="checkName()" />
          <div class="error-text">{{ nameErr() }}</div>
        </div>
        <div class="field">
          <label class="label" for="adm-email">מה האימייל של מנהל העיר?<span class="req">*</span></label>
          <input id="adm-email" name="adminEmail" type="email" dir="ltr" class="input" [(ngModel)]="adminEmail" [class.invalid]="emailErr()" (blur)="checkEmail()" />
          <div class="error-text">{{ emailErr() }}</div>
        </div>
        <div class="field">
          <label class="label" for="copy">להעתיק קהילות ושעות פעילות מעיר קיימת?</label>
          <select id="copy" name="copy" class="input" [(ngModel)]="copyFrom">
            <option [ngValue]="null">לא להעתיק</option>
            @for (c of cities(); track c.id) { <option [ngValue]="c.id">{{ c.name }}</option> }
          </select>
        </div>
        <p class="text-sm text-muted">מנהל העיר יקבל מייל עם קישור לקביעת סיסמה.</p>
        @if (createError()) { <p class="notice bad" role="alert">{{ createError() }}</p> }
        <div class="flex gap-2">
          <button class="btn-primary" type="submit" [disabled]="busy()">פתיחת העיר</button>
          <button class="btn-secondary" type="button" (click)="newOpen.set(false)">ביטול</button>
        </div>
      </form>
    </app-dialog>
  `,
})
export class CitiesPage implements OnInit {
  private readonly api = inject(Api);
  protected readonly steps = STEPS;
  protected readonly cities = signal<CityRow[]>([]);
  protected readonly localities = signal<ComboItem[]>([]);
  protected readonly notice = signal('');
  protected readonly newOpen = signal(false);
  protected readonly busy = signal(false);
  protected readonly createError = signal('');
  protected readonly locErr = signal('');
  protected readonly nameErr = signal('');
  protected readonly emailErr = signal('');
  protected localityCode: string | null = null;
  protected adminName = '';
  protected adminEmail = '';
  protected copyFrom: string | null = null;
  private searchSeq = 0;

  async ngOnInit() {
    await this.load();
  }

  private async load() {
    this.cities.set(await this.api.get<CityRow[]>('/api/admin/cities'));
  }

  protected done(c: CityRow) {
    return Object.values(c.setup).filter(Boolean).length;
  }

  protected openNew() {
    this.localityCode = this.copyFrom = null;
    this.adminName = this.adminEmail = '';
    this.localities.set([]);
    [this.locErr, this.nameErr, this.emailErr, this.createError].forEach((s) => s.set(''));
    this.newOpen.set(true);
  }

  protected async searchLocalities(q: string) {
    const seq = ++this.searchSeq;
    if (q.trim().length < 2) {
      this.localities.set([]);
      return;
    }
    try {
      const r = await this.api.get<{ code: number; name: string }[]>(`/api/admin/localities?q=${encodeURIComponent(q.trim())}`);
      if (seq === this.searchSeq) this.localities.set(r.map((l) => ({ id: String(l.code), label: l.name })));
    } catch (e) {
      this.locErr.set(errorMessage(e));
    }
  }

  protected checkLoc() {
    this.locErr.set(this.localityCode ? '' : 'צריך לבחור עיר מהרשימה');
    return !this.locErr();
  }

  protected checkName() {
    this.nameErr.set(this.adminName.trim() ? '' : 'צריך למלא שם');
    return !this.nameErr();
  }

  protected checkEmail() {
    const v = this.adminEmail.trim();
    this.emailErr.set(!v ? 'צריך למלא אימייל' : EMAIL.test(v) ? '' : 'האימייל לא תקין');
    return !this.emailErr();
  }

  protected async create() {
    if (![this.checkLoc(), this.checkName(), this.checkEmail()].every(Boolean)) return;
    this.busy.set(true);
    this.createError.set('');
    try {
      const r = await this.api.post<{ name: string }>('/api/admin/cities', {
        localityCode: +this.localityCode!, adminName: this.adminName, adminEmail: this.adminEmail, copyFromCityId: this.copyFrom,
      });
      this.newOpen.set(false);
      this.notice.set(`העיר ${r.name} נפתחה, ונשלחה הזמנה אל ${this.adminEmail.trim()}`);
      await this.load();
    } catch (e) {
      this.createError.set(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}
