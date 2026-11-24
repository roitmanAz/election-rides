import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Api } from '../core/api';
import { errorMessage } from '../core/auth.service';
import { Dialog } from '../shared/dialog';
import { Icon } from '../shared/icon';
import { Select, SelectOption } from '../shared/select';

interface Status {
  loaded: boolean;
  fileName: string | null;
  voterCount: number | null;
  loadedAt: string | null;
  loadedBy: string | null;
  deletionDate: string | null;
  deletionDaysSet: boolean;
  certificates: { id: string; number: number; fileName: string; voterCount: number; deletedAt: string; deletedBy: string }[];
}
interface Certificate {
  number: number;
  cityName: string;
  operationText: string | null;
  fileName: string;
  voterCount: number;
  deletedAt: string;
  deletedBy: string;
  whatWasDeleted: string;
  sentTo: string;
}

type FieldKey = 'idNumber' | 'firstName' | 'lastName' | 'ballotBox' | 'cluster' | 'age' | 'street' | 'buildingNumber';
const FIELDS: { key: FieldKey; label: string; required: boolean }[] = [
  { key: 'idNumber', label: 'מספר זהות', required: true },
  { key: 'firstName', label: 'שם פרטי', required: true },
  { key: 'lastName', label: 'שם משפחה', required: true },
  { key: 'ballotBox', label: 'מספר קלפי', required: true },
  { key: 'cluster', label: 'אשכול קלפיות', required: false },
  { key: 'age', label: 'גיל', required: false },
  { key: 'street', label: 'רחוב', required: false },
  { key: 'buildingNumber', label: 'מספר בניין', required: false },
];

/** Guess the matching column from its header (the city admin can change every choice). */
const GUESS: Record<FieldKey, RegExp> = {
  idNumber: /זהות|ת"ז|תז|ת.ז/,
  firstName: /פרטי/,
  lastName: /משפחה/,
  ballotBox: /קלפי/,
  cluster: /אשכול/,
  age: /גיל/,
  street: /רחוב/,
  buildingNumber: /בית|בניין|מס.? בית/,
};

/**
 * Voter base (spec 3.1, 8): status, the legal note, a Hebrew file button, column mapping (only these fields
 * are loaded), delete now with confirmation, and the deletion certificates.
 */
@Component({
  selector: 'app-voter-base',
  imports: [FormsModule, Dialog, Icon, DatePipe, Select],
  template: `
    <section class="card overflow-hidden">
      <div class="border-b border-line px-5 py-4"><h1 class="text-xl font-semibold">בייס מצביעים</h1></div>
      @if (s(); as st) {
        <div class="grid gap-4 p-5">
          <div class="flex flex-wrap items-center gap-3 rounded-2xl p-4" [class]="st.loaded ? 'bg-action-soft' : 'bg-soft'">
            @if (st.loaded) {
              <app-icon name="check" />
              <span><b>הבייס טעון:</b> {{ st.fileName }} · <span class="tabular-nums">{{ st.voterCount }}</span> מצביעים · נטען {{ st.loadedAt | date: 'dd.MM.yyyy HH:mm' }} על ידי {{ st.loadedBy }}</span>
              <button type="button" class="btn-secondary ms-auto h-10 text-bad" (click)="confirmDelete.set(true)"><app-icon name="trash" [size]="18" /> מחק את הבייס עכשיו</button>
            } @else {
              <span><b>אין בייס טעון.</b> בלי בייס, כל פנייה עוברת אימות ידני.</span>
            }
          </div>

          <p class="flex items-start gap-2 rounded-2xl bg-warn-soft p-4 text-sm text-warn">
            <app-icon name="warn" [size]="18" />
            <span>הבייס יימחק אוטומטית בסוף תקופת הבחירות, כולל הגיבויים.
              @if (st.deletionDate) { תאריך המחיקה: <b>{{ st.deletionDate | date: 'dd.MM.yyyy' }}</b>. }
              @else if (!st.deletionDaysSet) { מנהל המערכת עוד לא קבע אחרי כמה ימים מוחקים. }
              המערכת מפיקה אישור מחיקה, שומרת אותו כאן, ושולחת אותו במייל לממונה הפרטיות של המפלגה ולמנהל העיר. לפני טעינה צריך אישור של היועץ המשפטי של המפלגה.</span>
          </p>

          <div class="grid gap-2">
            <span class="label">{{ st.loaded ? 'לטעון קובץ חדש במקום הקיים?' : 'איזה קובץ לטעון?' }}</span>
            <div class="flex flex-wrap items-center gap-3">
              <label class="btn-secondary cursor-pointer">
                <app-icon name="plus" [size]="18" /> בחר קובץ
                <input type="file" class="sr-only" accept=".xlsx,.csv" (change)="pick($any($event.target).files?.[0])" />
              </label>
              <span class="text-sm" [class.text-muted]="!file()">{{ file()?.name ?? 'לא נבחר קובץ' }}</span>
            </div>
            <p class="text-sm text-muted">Excel ‏(xlsx) או CSV. נטענים רק השדות שבוחרים למטה, ושאר העמודות לא נשמרות.</p>
          </div>

          @if (busy()) { <p class="notice" role="status">{{ busy() }}</p> }
          @if (error()) { <p class="notice bad" role="alert">{{ error() }}</p> }

          @if (headers().length) {
            <div class="grid gap-3 rounded-2xl border-[1.5px] border-line p-4">
              <h2 class="font-semibold">איזו עמודה בקובץ מתאימה לכל שדה? <span class="text-sm font-normal text-muted">({{ rowCount() }} שורות)</span></h2>
              <div class="grid gap-3 sm:grid-cols-2">
                @for (f of fields; track f.key) {
                  <label class="field"><span class="label">{{ f.label }}@if (f.required) { <span class="req">*</span> }</span>
                    <app-select [options]="f.required ? requiredCols() : optionalCols()" placeholder="בחר עמודה"
                      [invalid]="f.required && tried() && map[f.key] === null" [(ngModel)]="map[f.key]" /></label>
                }
              </div>
              <fieldset class="field">
                <legend class="label mb-1.5">אילו עמודות הן טלפונים?</legend>
                <div class="flex flex-wrap gap-2">
                  @for (h of headers(); track $index) {
                    <label class="inline-flex h-10 cursor-pointer items-center gap-2 rounded-full bg-soft px-3 text-sm" [class.!bg-action-soft]="phones.has($index)">
                      <input type="checkbox" class="size-4" [checked]="phones.has($index)" (change)="togglePhone($index)" /> {{ h || 'עמודה ' + ($index + 1) }}</label>
                  }
                </div>
              </fieldset>
              @if (sample().length) {
                <div class="overflow-x-auto"><table class="min-w-full text-xs">
                  <thead><tr>@for (h of headers(); track $index) { <th class="whitespace-nowrap px-2 py-1 text-start font-medium text-muted">{{ h }}</th> }</tr></thead>
                  <tbody>@for (r of sample(); track $index) { <tr class="border-t border-line">@for (c of r; track $index) { <td class="whitespace-nowrap px-2 py-1">{{ c }}</td> }</tr> }</tbody>
                </table></div>
              }
              <div><button type="button" class="btn-primary" [disabled]="!!busy()" (click)="doImport()">טען את הבייס</button></div>
            </div>
          }

          @if (result(); as r) {
            <div class="notice grid gap-1" role="status">
              <b>נטענו {{ r.imported }} מצביעים.</b>
              @if (r.skipped) { <span>דולגו {{ r.skipped }} שורות: @for (k of skipKeys(r); track k) { {{ k }} ({{ r.skipReasons[k] }}) } </span> }
              @if (r.boxesWithoutCluster.length) { <span class="text-warn">קלפיות שלא שייכות לאף אשכול: {{ r.boxesWithoutCluster.join(', ') }}. אפשר להוסיף אותן במסך האשכולות.</span> }
            </div>
          }

          <div class="grid gap-2">
            <h2 class="font-semibold">אישורי מחיקה</h2>
            @for (c of st.certificates; track c.id) {
              <div class="flex flex-wrap items-center gap-3 rounded-xl bg-bg px-3 py-2 text-sm">
                <b>מס' {{ c.number }}</b><span>{{ c.fileName }}</span><span class="tabular-nums">{{ c.voterCount }} מצביעים</span>
                <span class="text-muted">{{ c.deletedAt | date: 'dd.MM.yyyy HH:mm' }} · {{ c.deletedBy }}</span>
                <button type="button" class="btn-secondary ms-auto h-9 px-3 text-sm" (click)="openCert(c.id)">הצג</button>
              </div>
            } @empty { <p class="text-sm text-muted">עוד לא נמחק בייס בעיר.</p> }
          </div>
        </div>
      }
    </section>

    <app-dialog [open]="confirmDelete()" title="מחיקת הבייס" [width]="460" (closed)="confirmDelete.set(false)">
      <p>למחוק עכשיו את כל המצביעים שבבייס? פניות שכבר נקלטו לא נמחקות. יופק אישור מחיקה, וישלח לממונה הפרטיות ולמנהל העיר.</p>
      <div class="mt-5 flex gap-2">
        <button type="button" class="btn-primary !bg-bad" (click)="doDelete()">מחק את הבייס</button>
        <button type="button" class="btn-secondary" (click)="confirmDelete.set(false)">ביטול</button>
      </div>
    </app-dialog>

    <app-dialog [open]="!!cert()" [title]="'אישור מחיקה מס\\' ' + (cert()?.number ?? '')" [width]="560" (closed)="cert.set(null)">
      @if (cert(); as c) {
        <dl class="grid grid-cols-[110px_minmax(0,1fr)] gap-x-3 gap-y-2 text-sm">
          <dt class="text-muted">עיר</dt><dd>{{ c.cityName }}</dd>
          <dt class="text-muted">בחירות</dt><dd>{{ c.operationText ?? '-' }}</dd>
          <dt class="text-muted">קובץ</dt><dd>{{ c.fileName }}</dd>
          <dt class="text-muted">מספר מצביעים</dt><dd class="tabular-nums">{{ c.voterCount }}</dd>
          <dt class="text-muted">מתי נמחק</dt><dd>{{ c.deletedAt | date: 'dd.MM.yyyy HH:mm' }}</dd>
          <dt class="text-muted">מי מחק</dt><dd>{{ c.deletedBy }}</dd>
          <dt class="text-muted">מה נמחק</dt><dd>{{ c.whatWasDeleted }}</dd>
          <dt class="text-muted">נשלח אל</dt><dd dir="ltr" class="text-right">{{ c.sentTo }}</dd>
        </dl>
      }
    </app-dialog>
  `,
})
export class VoterBasePage implements OnInit {
  private readonly api = inject(Api);
  protected readonly fields = FIELDS;
  protected readonly s = signal<Status | null>(null);
  protected readonly file = signal<File | null>(null);
  protected readonly headers = signal<string[]>([]);
  private readonly columns = computed<SelectOption[]>(() => this.headers().map((h, i) => ({ value: i, label: h || 'עמודה ' + (i + 1) })));
  protected readonly requiredCols = computed(() => this.columns());
  protected readonly optionalCols = computed<SelectOption[]>(() => [{ value: null, label: 'אין בקובץ' }, ...this.columns()]);
  protected readonly sample = signal<string[][]>([]);
  protected readonly rowCount = signal(0);
  protected readonly busy = signal('');
  protected readonly error = signal('');
  protected readonly tried = signal(false);
  protected readonly result = signal<{ imported: number; skipped: number; skipReasons: Record<string, number>; boxesWithoutCluster: string[] } | null>(null);
  protected readonly confirmDelete = signal(false);
  protected readonly cert = signal<Certificate | null>(null);
  protected map: Record<FieldKey, number | null> = this.emptyMap();
  protected phones = new Set<number>();

  async ngOnInit() {
    await this.load();
  }

  private async load() {
    this.s.set(await this.api.get<Status>('/api/city/base'));
  }

  private emptyMap(): Record<FieldKey, number | null> {
    return { idNumber: null, firstName: null, lastName: null, ballotBox: null, cluster: null, age: null, street: null, buildingNumber: null };
  }

  protected skipKeys(r: { skipReasons: Record<string, number> }) {
    return Object.keys(r.skipReasons);
  }

  protected async pick(f: File | undefined) {
    this.file.set(f ?? null);
    this.headers.set([]);
    this.result.set(null);
    this.error.set('');
    if (!f) return;
    const form = new FormData();
    form.append('file', f);
    this.busy.set('קורא את הקובץ...');
    try {
      const p = await this.api.post<{ headers: string[]; sample: string[][]; rowCount: number }>('/api/city/base/preview', form);
      this.headers.set(p.headers);
      this.sample.set(p.sample);
      this.rowCount.set(p.rowCount);
      this.map = this.emptyMap();
      for (const f2 of FIELDS) {
        const i = p.headers.findIndex((h) => GUESS[f2.key].test(h));
        if (i >= 0) this.map[f2.key] = i;
      }
      this.phones = new Set(p.headers.map((h, i) => (/טלפון|נייד|פלאפון|סלולרי/.test(h) ? i : -1)).filter((i) => i >= 0));
    } catch (e) {
      this.error.set(errorMessage(e));
    } finally {
      this.busy.set('');
    }
  }

  protected togglePhone(i: number) {
    this.phones.has(i) ? this.phones.delete(i) : this.phones.add(i);
  }

  protected async doImport() {
    this.tried.set(true);
    const f = this.file();
    if (!f) return;
    if (FIELDS.some((x) => x.required && this.map[x.key] === null)) return this.error.set('צריך להתאים עמודה לכל שדה חובה');
    const form = new FormData();
    form.append('file', f);
    form.append('mapping', JSON.stringify({ ...this.map, phones: [...this.phones] }));
    this.busy.set('טוען את הבייס. בקובץ גדול זה יכול לקחת כמה דקות...');
    this.error.set('');
    try {
      this.result.set(await this.api.post('/api/city/base/import', form));
      this.headers.set([]);
      this.file.set(null);
      await this.load();
    } catch (e) {
      this.error.set(errorMessage(e));
    } finally {
      this.busy.set('');
    }
  }

  protected async doDelete() {
    try {
      await this.api.delete('/api/city/base');
      this.confirmDelete.set(false);
      this.result.set(null);
      await this.load();
    } catch (e) {
      this.error.set(errorMessage(e));
      this.confirmDelete.set(false);
    }
  }

  protected async openCert(id: string) {
    this.cert.set(await this.api.get<Certificate>(`/api/city/base/certificates/${id}`));
  }
}
