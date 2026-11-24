import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { Api } from '../core/api';
import { AuthService, errorMessage } from '../core/auth.service';
import { Dialog } from '../shared/dialog';
import { Icon } from '../shared/icon';
import { Select, SelectOption, dayText } from '../shared/select';
import { markHtml } from '../shared/mark';
import { RowMenu } from '../shared/row-menu';

const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

// ---------------------------------------------------------------- Live status
interface Live {
  active: boolean;
  operation?: { type: string; electionDay: string };
  totals?: { cities: number; requestsToday: number; queued: number; longestWait: number; longestWaitCity: string | null; freeDrivers: number; onTrip: number };
  alerts?: { level: 'bad' | 'warn'; city: string; text: string }[];
  cities?: { id: string; name: string; requestsToday: number; queued: number; longestWait: number; freeDrivers: number; onTrip: number; hasAdmin: boolean; adminLastLogin: string | null }[];
}

/** First screen of the system admin (spec 3.2): totals, alerts, and a row per city. Counts only. Refreshes every minute. */
@Component({
  selector: 'app-live',
  imports: [Icon, DatePipe],
  template: `
    @if (v(); as l) {
      @if (!l.active) {
        <p class="notice">אין עכשיו מבצע בחירות פעיל. אפשר לפתוח מבצע במסך "מבצעי בחירות".</p>
      } @else {
        <div class="grid gap-4">
          <div class="grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-6">
            @for (t of tiles(l); track t.label) {
              <div class="card p-4"><div class="text-sm font-medium text-muted">{{ t.label }}</div>
                <div class="mt-1 text-3xl font-semibold tabular-nums" [class.text-bad]="t.bad">{{ t.value }}</div>
                @if (t.sub) { <div class="text-sm text-muted">{{ t.sub }}</div> }</div>
            }
          </div>
          <section class="card p-5">
            <h2 class="mb-3 text-lg font-semibold">התראות</h2>
            @for (a of l.alerts; track $index) {
              <p class="mb-2 flex items-center gap-2 rounded-xl px-3 py-2" [class]="a.level === 'bad' ? 'bg-bad-soft text-bad' : 'bg-warn-soft text-warn'">
                <app-icon name="warn" [size]="16" /><b>{{ a.city }}:</b> {{ a.text }}</p>
            } @empty { <p class="flex items-center gap-2 text-ok"><app-icon name="check" [size]="18" /> כל הערים עובדות כרגיל</p> }
          </section>
          <section class="card overflow-hidden">
            <div class="border-b border-line px-5 py-3 font-semibold">לפי עיר</div>
            <div class="p-3">
              <div class="hidden grid-cols-[minmax(0,1.3fr)_repeat(5,minmax(0,1fr))_minmax(0,1.3fr)] gap-2 px-3 pb-2 text-sm font-medium text-muted md:grid">
                <span>עיר</span><span>פניות</span><span>ממתינים</span><span>המתנה ארוכה</span><span>נהגים פנויים</span><span>בנסיעה</span><span>מנהל העיר נכנס</span>
              </div>
              @for (c of l.cities; track c.id) {
                <div class="grid grid-cols-2 gap-2 rounded-xl px-3 py-2 odd:bg-bg md:grid-cols-[minmax(0,1.3fr)_repeat(5,minmax(0,1fr))_minmax(0,1.3fr)]">
                  <b class="col-span-2 md:col-span-1">{{ c.name }}</b>
                  <span class="tabular-nums"><span class="text-muted md:hidden">פניות: </span>{{ c.requestsToday }}</span>
                  <span class="tabular-nums"><span class="text-muted md:hidden">ממתינים: </span>{{ c.queued }}</span>
                  <span><span class="text-muted md:hidden">המתנה ארוכה: </span><span class="rounded-[8px] px-2 font-semibold tabular-nums"
                    [class]="c.longestWait >= 30 ? 'bg-bad text-white' : c.longestWait >= 15 ? 'bg-warn-soft text-warn' : 'bg-ok-soft text-ok'">{{ c.longestWait }}</span></span>
                  <span class="tabular-nums"><span class="text-muted md:hidden">פנויים: </span>{{ c.freeDrivers }}</span>
                  <span class="tabular-nums"><span class="text-muted md:hidden">בנסיעה: </span>{{ c.onTrip }}</span>
                  <span class="text-sm">@if (!c.hasAdmin) { <span class="text-bad">אין מנהל</span> } @else if (c.adminLastLogin) { {{ c.adminLastLogin | date: 'dd.MM HH:mm' }} } @else { <span class="text-muted">עוד לא נכנס</span> }</span>
                </div>
              }
            </div>
          </section>
        </div>
      }
    }
  `,
})
export class LivePage implements OnInit, OnDestroy {
  private readonly api = inject(Api);
  protected readonly v = signal<Live | null>(null);
  private timer?: ReturnType<typeof setInterval>;

  async ngOnInit() {
    await this.load();
    this.timer = setInterval(() => this.load(), 60_000);
  }
  ngOnDestroy() {
    clearInterval(this.timer);
  }
  private async load() {
    this.v.set(await this.api.get<Live>('/api/admin/live'));
  }
  protected tiles(l: Live) {
    const t = l.totals!;
    return [
      { label: 'ערים במבצע', value: t.cities, sub: '', bad: false },
      { label: 'פניות היום', value: t.requestsToday, sub: '', bad: false },
      { label: 'ממתינים', value: t.queued, sub: '', bad: false },
      { label: 'ההמתנה הארוכה', value: t.longestWait, sub: t.longestWaitCity ?? '', bad: t.longestWait >= 30 },
      { label: 'נהגים פנויים', value: t.freeDrivers, sub: '', bad: false },
      { label: 'נהגים בנסיעה', value: t.onTrip, sub: '', bad: false },
    ];
  }
}

// ---------------------------------------------------------------- Reports
interface Report {
  id: string;
  type: string;
  electionDay: string;
  cities: { city: string; requests: number; arrivedPercent: number; averageWait: number; longestWait: number; cancelled: number; peakHour: string | null; waitChange: number | null }[];
}

/** Reports (spec 3.2): per city for the chosen operation, with the change against the previous one; Excel export. */
@Component({
  selector: 'app-reports',
  imports: [FormsModule, Select],
  template: `
    <section class="card overflow-hidden">
      <div class="flex flex-wrap items-center gap-3 border-b border-line px-5 py-4">
        <h1 class="text-xl font-semibold">דוחות</h1>
        <app-select class="ms-auto w-56" ariaLabel="איזה מבצע?" [options]="opOptions()" [ngModel]="opId()" (ngModelChange)="choose($event)" />
        <button type="button" class="btn-secondary" [disabled]="!r()" (click)="excel()">ייצוא ל-Excel</button>
      </div>
      @if (r(); as rep) {
        <div class="overflow-x-auto p-3">
          <table class="w-full min-w-[720px] text-sm">
            <thead><tr class="text-muted">@for (h of heads; track h) { <th class="px-3 py-2 text-start font-medium">{{ h }}</th> }</tr></thead>
            <tbody>
              @for (c of rep.cities; track c.city) {
                <tr class="border-t border-line">
                  <td class="px-3 py-2 font-medium">{{ c.city }}</td><td class="px-3 py-2 tabular-nums">{{ c.requests }}</td>
                  <td class="px-3 py-2 tabular-nums">{{ c.arrivedPercent }}%</td><td class="px-3 py-2 tabular-nums">{{ c.averageWait }}</td>
                  <td class="px-3 py-2 tabular-nums">{{ c.longestWait }}</td><td class="px-3 py-2 tabular-nums">{{ c.cancelled }}</td>
                  <td class="px-3 py-2 tabular-nums">{{ c.peakHour ?? '-' }}</td>
                  <td class="px-3 py-2 tabular-nums">
                    @if (c.waitChange === null) { <span class="text-muted">-</span> }
                    @else { <span [class]="c.waitChange <= 0 ? 'text-ok' : 'text-bad'">{{ c.waitChange > 0 ? '+' : '' }}{{ c.waitChange }} דק'</span> }
                  </td>
                </tr>
              }
            </tbody>
          </table>
          <p class="mt-2 text-sm text-muted">המתנה: מקבלת הפנייה עד שנמצא לה נהג, בדקות. שינוי: לעומת המבצע הקודם (ירוק כשהשתפר, אדום כשהורע).</p>
        </div>
      } @else { <p class="p-5 text-muted">אין מבצעים עדיין.</p> }
    </section>
  `,
})
export class ReportsPage implements OnInit {
  private readonly api = inject(Api);
  private readonly http = inject(HttpClient);
  protected readonly heads = ['עיר', 'פניות', 'הגיעו לקלפי', 'המתנה ממוצעת', 'המתנה ארוכה', 'ביטולים', 'שעת השיא', 'שינוי בהמתנה'];
  protected readonly ops = signal<{ id: string; type: string; electionDay: string }[]>([]);
  protected readonly opId = signal('');
  protected readonly opOptions = computed<SelectOption[]>(() =>
    this.ops().map((o) => ({ value: o.id, label: `${o.type === 'Knesset' ? 'כנסת' : 'רשויות'} ${dayText(o.electionDay)}` })));
  protected readonly r = signal<Report | null>(null);

  async ngOnInit() {
    const ops = await this.api.get<{ id: string; type: string; electionDay: string }[]>('/api/admin/operations');
    this.ops.set(ops);
    if (ops.length) await this.choose(ops[0].id);
  }
  protected async choose(id: string) {
    this.opId.set(id);
    this.r.set(await this.api.get<Report>(`/api/admin/reports/${id}`));
  }
  protected async excel() {
    const blob = await firstValueFrom(this.http.get(`/api/admin/reports/${this.opId()}/excel`, { responseType: 'blob' }));
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = `דוח-${this.r()!.electionDay}.xlsx`;
    a.click();
    URL.revokeObjectURL(a.href);
  }
}

// ---------------------------------------------------------------- Certificates
interface CertRow { id: string; number: number; cityName: string; fileName: string; voterCount: number; deletedAt: string; deletedBy: string }
interface Cert extends CertRow { operationText: string | null; whatWasDeleted: string; sentTo: string }

/** Deletion certificates from all cities (spec 3.2), with a view of each. */
@Component({
  selector: 'app-certificates',
  imports: [Dialog, DatePipe],
  template: `
    <section class="card overflow-hidden">
      <div class="border-b border-line px-5 py-4"><h1 class="text-xl font-semibold">אישורי מחיקה</h1></div>
      <div class="grid gap-2 p-3 sm:p-5">
        @for (c of list(); track c.id) {
          <div class="flex flex-wrap items-center gap-3 rounded-xl bg-bg px-3 py-2 text-sm">
            <b>מס' {{ c.number }}</b><span>{{ c.cityName }}</span><span>{{ c.fileName }}</span><span class="tabular-nums">{{ c.voterCount }} מצביעים</span>
            <span class="text-muted">{{ c.deletedAt | date: 'dd.MM.yyyy HH:mm' }} · {{ c.deletedBy }}</span>
            <button type="button" class="btn-secondary ms-auto h-9 px-3 text-sm" (click)="open(c.id)">הצג</button>
          </div>
        } @empty { <p class="text-muted">עוד לא נמחק אף בייס.</p> }
      </div>
    </section>
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
export class CertificatesPage implements OnInit {
  private readonly api = inject(Api);
  protected readonly list = signal<CertRow[]>([]);
  protected readonly cert = signal<Cert | null>(null);
  async ngOnInit() {
    this.list.set(await this.api.get<CertRow[]>('/api/admin/certificates'));
  }
  protected async open(id: string) {
    this.cert.set(await this.api.get<Cert>(`/api/admin/certificates/${id}`));
  }
}

// ---------------------------------------------------------------- Audit log
interface AuditRow { id: number; at: string; userEmail: string | null; city: string | null; action: string; details: string | null }

/**
 * Audit log (spec 3.2): 100 rows at a time with "load more"; search over everything shown
 * (action, details, email, city), with the searched text marked; filter by city. Entries cannot be deleted.
 */
@Component({
  selector: 'app-audit',
  imports: [FormsModule, DatePipe, Select],
  template: `
    <section class="card overflow-hidden">
      <div class="flex flex-wrap items-center gap-3 border-b border-line px-5 py-4">
        <h1 class="text-xl font-semibold">יומן פעולות</h1>
        <app-select class="ms-auto w-48" ariaLabel="איזו עיר?" [options]="cityOptions()" [(ngModel)]="cityId" (ngModelChange)="load()" />
        <input class="input w-64 max-w-full" placeholder="חיפוש בפעולות" aria-label="חיפוש" [(ngModel)]="q" (ngModelChange)="typed()" />
      </div>
      <div class="p-3 text-sm">
        @for (a of rows(); track a.id) {
          <div class="grid gap-1 rounded-xl px-3 py-2 odd:bg-bg md:grid-cols-[120px_minmax(0,1fr)_minmax(0,1fr)_minmax(0,2fr)]">
            <span class="tabular-nums text-muted">{{ a.at | date: 'dd.MM HH:mm:ss' }}</span>
            <span class="truncate" dir="ltr" style="text-align: right" [innerHTML]="mark(a.userEmail ?? 'המערכת')"></span>
            <span [innerHTML]="mark(a.city ?? '-')"></span>
            <span><b [innerHTML]="mark(a.action)"></b>@if (a.details) { · <span [innerHTML]="mark(a.details)"></span> }</span>
          </div>
        } @empty { <p class="p-3 text-muted">{{ loading() ? 'טוען...' : 'אין רשומות.' }}</p> }
        <div class="flex flex-wrap items-center gap-3 px-3 pt-3">
          @if (more()) { <button type="button" class="btn-secondary" [disabled]="loading()" (click)="loadMore()">טעינת עוד 100</button> }
          <span class="text-muted">מוצגות {{ rows().length }} רשומות. אי אפשר למחוק רשומות מהיומן.</span>
        </div>
      </div>
    </section>
  `,
})
export class AuditPage implements OnInit {
  private readonly api = inject(Api);
  protected readonly rows = signal<AuditRow[]>([]);
  protected readonly more = signal(false);
  protected readonly loading = signal(false);
  protected readonly cities = signal<{ id: string; name: string }[]>([]);
  protected readonly cityOptions = computed<SelectOption[]>(() => [{ value: null, label: 'כל הערים' }, ...this.cities().map((c) => ({ value: c.id, label: c.name }))]);
  protected cityId: string | null = null;
  protected q = '';
  /** The search the shown rows belong to; the marks follow it, not the box while a new search loads. */
  private readonly shownQ = signal('');
  private seq = 0;
  private timer?: ReturnType<typeof setTimeout>;

  async ngOnInit() {
    this.cities.set(await this.api.get<{ id: string; name: string }[]>('/api/admin/cities'));
    await this.load();
  }
  protected mark(text: string) {
    return markHtml(text, this.shownQ());
  }
  /** Waits a moment after typing, so each letter does not send its own search. */
  protected typed() {
    clearTimeout(this.timer);
    this.timer = setTimeout(() => this.load(), 250);
  }
  private async fetch(before: number | null) {
    const params = new URLSearchParams();
    if (this.cityId) params.set('cityId', this.cityId);
    if (this.q.trim()) params.set('q', this.q.trim());
    if (before !== null) params.set('before', String(before));
    return this.api.get<{ rows: AuditRow[]; more: boolean }>(`/api/admin/audit?${params}`);
  }
  protected async load() {
    const seq = ++this.seq;
    this.loading.set(true);
    try {
      const r = await this.fetch(null);
      if (seq !== this.seq) return;
      this.shownQ.set(this.q.trim());
      this.rows.set(r.rows);
      this.more.set(r.more);
    } finally {
      if (seq === this.seq) this.loading.set(false);
    }
  }
  protected async loadMore() {
    const last = this.rows().at(-1);
    if (!last) return;
    const seq = this.seq;
    this.loading.set(true);
    try {
      const r = await this.fetch(last.id);
      if (seq !== this.seq) return;
      this.rows.update((x) => [...x, ...r.rows]);
      this.more.set(r.more);
    } finally {
      this.loading.set(false);
    }
  }
}

// ---------------------------------------------------------------- System admins
/** System admins (spec 3.2): add by invitation, remove with confirmation; never yourself, and one always remains. */
@Component({
  selector: 'app-sysadmins',
  imports: [FormsModule, Dialog, Icon, DatePipe],
  template: `
    <section class="card overflow-hidden">
      <div class="flex flex-wrap items-center gap-3 border-b border-line px-5 py-4">
        <h1 class="text-xl font-semibold">מנהלי מערכת</h1>
        <button type="button" class="btn-primary ms-auto" (click)="openAdd()"><app-icon name="plus" [size]="18" /> מנהל מערכת חדש</button>
      </div>
      @if (notice()) { <p class="notice mx-5 mt-4">{{ notice() }}</p> }
      <div class="grid gap-1 p-3 sm:p-5">
        @for (u of list(); track u.id) {
          <div class="flex flex-wrap items-center gap-3 rounded-xl px-3 py-2 odd:bg-bg">
            <b>{{ u.fullName }}</b><span class="text-muted" dir="ltr">{{ u.email }}</span>
            <span class="text-sm">@if (u.invited) { <span class="text-warn">הוזמן, עוד לא נכנס</span> } @else if (u.lastLoginAt) { נכנס {{ u.lastLoginAt | date: 'dd.MM.yy HH:mm' }} }</span>
            @if (u.id !== auth.me()?.id) {
              <button type="button" class="btn-secondary ms-auto h-9 px-3 text-sm text-bad" (click)="toRemove.set(u)">הסר</button>
            }
          </div>
        }
      </div>
    </section>
    <app-dialog [open]="addOpen()" title="מנהל מערכת חדש" [width]="460" (closed)="addOpen.set(false)">
      <div class="grid gap-3">
        <label class="field"><span class="label">מה השם המלא?<span class="req">*</span></span><input class="input" [(ngModel)]="name" /></label>
        <label class="field"><span class="label">מה האימייל?<span class="req">*</span></span><input class="input" type="email" dir="ltr" [(ngModel)]="email" /></label>
        @if (err()) { <p class="notice bad">{{ err() }}</p> }
        <div class="flex gap-2"><button type="button" class="btn-primary" (click)="add()">שליחת הזמנה</button><button type="button" class="btn-secondary" (click)="addOpen.set(false)">ביטול</button></div>
      </div>
    </app-dialog>
    <app-dialog [open]="!!toRemove()" title="הסרת מנהל מערכת" [width]="440" (closed)="toRemove.set(null)">
      <p>להסיר את {{ toRemove()?.fullName }}?</p>
      @if (err()) { <p class="notice bad mt-3">{{ err() }}</p> }
      <div class="mt-5 flex gap-2"><button type="button" class="btn-primary !bg-bad" (click)="remove()">הסר</button><button type="button" class="btn-secondary" (click)="toRemove.set(null)">ביטול</button></div>
    </app-dialog>
  `,
})
export class SysAdminsPage implements OnInit {
  private readonly api = inject(Api);
  protected readonly auth = inject(AuthService);
  protected readonly list = signal<{ id: string; fullName: string; email: string; lastLoginAt: string | null; invited: boolean }[]>([]);
  protected readonly addOpen = signal(false);
  protected readonly toRemove = signal<{ id: string; fullName: string } | null>(null);
  protected readonly err = signal('');
  protected readonly notice = signal('');
  protected name = '';
  protected email = '';
  async ngOnInit() {
    await this.load();
  }
  private async load() {
    this.list.set(await this.api.get('/api/admin/sysadmins'));
  }
  protected openAdd() {
    this.name = this.email = '';
    this.err.set('');
    this.addOpen.set(true);
  }
  protected async add() {
    if (!this.name.trim()) return this.err.set('צריך למלא שם');
    if (!EMAIL.test(this.email.trim())) return this.err.set('האימייל לא תקין');
    try {
      await this.api.post('/api/admin/sysadmins', { email: this.email, fullName: this.name });
      this.addOpen.set(false);
      this.notice.set(`נשלחה הזמנה אל ${this.email.trim()}`);
      await this.load();
    } catch (e) {
      this.err.set(errorMessage(e));
    }
  }
  protected async remove() {
    const u = this.toRemove();
    if (!u) return;
    try {
      await this.api.delete(`/api/admin/sysadmins/${u.id}`);
      this.toRemove.set(null);
      await this.load();
    } catch (e) {
      this.err.set(errorMessage(e));
    }
  }
}

// ---------------------------------------------------------------- Security policy
/** Security policy for all users in all cities (spec 3.2); applies from the next sign-in. */
@Component({
  selector: 'app-security',
  imports: [FormsModule, Select],
  template: `
    <section class="card max-w-2xl overflow-hidden">
      <div class="border-b border-line px-5 py-4"><h1 class="text-xl font-semibold">מדיניות אבטחה</h1></div>
      @if (pol(); as p) {
        <div class="grid gap-4 p-5">
          <fieldset class="field">
            <legend class="label mb-1.5">לבקש קוד במייל בכניסה ממכשיר חדש?</legend>
            <div class="flex w-max gap-1 rounded-xl bg-soft p-1">
              <button type="button" class="h-10 rounded-[10px] px-5 font-medium" [class]="p.requireEmailCode ? 'bg-ink text-white' : ''" (click)="p.requireEmailCode = true">כן</button>
              <button type="button" class="h-10 rounded-[10px] px-5 font-medium" [class]="!p.requireEmailCode ? 'bg-ink text-white' : ''" (click)="p.requireEmailCode = false">לא</button>
            </div>
            @if (!p.requireEmailCode) { <p class="text-sm font-medium text-bad">בלי קוד במייל, סיסמה שדלפה מספיקה כדי להיכנס. לא מומלץ.</p> }
          </fieldset>
          <label class="field"><span class="label">כמה זמן המכשיר נזכר?</span>
            <app-select class="w-48" [options]="rememberOpts" [(ngModel)]="p.rememberDeviceHours" /></label>
          <label class="field"><span class="label">אחרי כמה זמן בלי פעילות מנתקים?</span>
            <app-select class="w-48" [options]="idleOpts" [(ngModel)]="p.idleTimeoutMinutes" />
            <span class="text-sm text-muted">ביום הבחירות כדאי ארוך יותר, כדי לא לנתק מוקדנים באמצע עבודה.</span></label>
          <label class="field"><span class="label">אחרי כמה ניסיונות כניסה שגויים נועלים?</span>
            <app-select class="w-48" [options]="attemptOpts" [(ngModel)]="p.maxFailedLogins" /></label>
          <label class="field"><span class="label">לכמה זמן נועלים?</span>
            <app-select class="w-48" [options]="lockOpts" [(ngModel)]="p.lockMinutes" />
            <span class="text-sm text-muted">מנהל העיר יכול לשחרר נעילה מוקדם.</span></label>
          @if (msg()) { <p class="notice" [class.bad]="bad()">{{ msg() }}</p> }
          <p class="text-sm text-muted">שינוי חל מהכניסה הבאה של כל משתמש, ונרשם ביומן.</p>
          <div><button type="button" class="btn-primary" (click)="save()">שמירה</button></div>
        </div>
      }
    </section>
  `,
})
export class SecurityPage implements OnInit {
  private readonly api = inject(Api);
  protected readonly rememberOpts = [{ value: 8, label: '8 שעות' }, { value: 24, label: 'יום' }, { value: 168, label: 'שבוע' }];
  protected readonly idleOpts = [{ value: 15, label: '15 דקות' }, { value: 30, label: '30 דקות' }, { value: 60, label: 'שעה' }, { value: 240, label: '4 שעות' }];
  protected readonly attemptOpts = [3, 5, 10].map((n) => ({ value: n, label: String(n) }));
  protected readonly lockOpts = [{ value: 5, label: '5 דקות' }, { value: 15, label: '15 דקות' }, { value: 30, label: '30 דקות' }, { value: 60, label: 'שעה' }];
  protected readonly pol = signal<{ requireEmailCode: boolean; rememberDeviceHours: number; idleTimeoutMinutes: number; maxFailedLogins: number; lockMinutes: number } | null>(null);
  protected readonly msg = signal('');
  protected readonly bad = signal(false);
  async ngOnInit() {
    this.pol.set(await this.api.get('/api/admin/security'));
  }
  protected async save() {
    try {
      await this.api.put('/api/admin/security', this.pol());
      this.bad.set(false);
      this.msg.set('המדיניות נשמרה');
    } catch (e) {
      this.bad.set(true);
      this.msg.set(errorMessage(e));
    }
  }
}

// ---------------------------------------------------------------- Party settings
interface PartySettings { partyName: string | null; privacyOfficerName: string | null; privacyOfficerEmail: string | null; legalAdvisorEmail: string | null; baseDeletionDays: number | null }
/** Privacy and party settings (spec 3.2): privacy officer, legal advisor, and the days until bases are deleted. */
@Component({
  selector: 'app-party',
  imports: [FormsModule, DatePipe],
  template: `
    <section class="card max-w-2xl overflow-hidden">
      <div class="border-b border-line px-5 py-4"><h1 class="text-xl font-semibold">פרטיות והגדרות</h1></div>
      @if (party(); as p) {
        <div class="grid gap-4 p-5 sm:grid-cols-2">
          <label class="field sm:col-span-2"><span class="label">מה שם המפלגה?</span><input class="input" [(ngModel)]="p.partyName" /></label>
          <label class="field"><span class="label">מי ממונה הפרטיות?</span><input class="input" [(ngModel)]="p.privacyOfficerName" /></label>
          <label class="field"><span class="label">מה האימייל של ממונה הפרטיות?</span><input class="input" type="email" dir="ltr" [(ngModel)]="p.privacyOfficerEmail" />
            <span class="text-sm text-muted">לשם נשלחים אישורי המחיקה.</span></label>
          <label class="field sm:col-span-2"><span class="label">מה האימייל של היועץ המשפטי?</span><input class="input max-w-sm" type="email" dir="ltr" [(ngModel)]="p.legalAdvisorEmail" /></label>
          <label class="field sm:col-span-2"><span class="label">כמה ימים אחרי יום הבחירות נמחקים הבייסים?</span>
            <input class="input w-32" type="number" min="1" max="365" [(ngModel)]="p.baseDeletionDays" />
            <span class="text-sm text-muted">לפי היועץ המשפטי. @if (deletionDate()) { במבצע הפעיל: {{ deletionDate() | date: 'dd.MM.yyyy' }}. } @if (!p.baseDeletionDays) { כל עוד לא נקבע, אין מחיקה אוטומטית. }</span></label>
          @if (msg()) { <p class="notice sm:col-span-2" [class.bad]="bad()">{{ msg() }}</p> }
          <div class="sm:col-span-2"><button type="button" class="btn-primary" (click)="save()">שמירה</button></div>
        </div>
      }
    </section>
  `,
})
export class PartyPage implements OnInit {
  private readonly api = inject(Api);
  protected readonly party = signal<PartySettings | null>(null);
  protected readonly deletionDate = signal<string | null>(null);
  protected readonly msg = signal('');
  protected readonly bad = signal(false);
  async ngOnInit() {
    const r = await this.api.get<PartySettings & { deletionDate: string | null }>('/api/admin/party');
    this.deletionDate.set(r.deletionDate);
    this.party.set(r);
  }
  protected async save() {
    const p = this.party()!;
    if ([p.privacyOfficerEmail, p.legalAdvisorEmail].some((e) => e?.trim() && !EMAIL.test(e.trim()))) {
      this.bad.set(true);
      return this.msg.set('אחד האימיילים לא תקין');
    }
    try {
      await this.api.put('/api/admin/party', { ...p, baseDeletionDays: p.baseDeletionDays || null });
      const r = await this.api.get<{ deletionDate: string | null }>('/api/admin/party');
      this.deletionDate.set(r.deletionDate);
      this.bad.set(false);
      this.msg.set('ההגדרות נשמרו');
    } catch (e) {
      this.bad.set(true);
      this.msg.set(errorMessage(e));
    }
  }
}

// ---------------------------------------------------------------- Shared community list
interface CatalogRow { id: number; name: string; cities: number }
interface RenameRequest { id: string; catalogId: number; oldName: string; newName: string; city: string | null; requestedAt: string; by: string }

/**
 * The shared community list (spec 3.2): rename requests from city admins to approve or reject,
 * and the list itself, where the system admin adds and renames directly (the change reaches every city).
 */
@Component({
  selector: 'app-catalog',
  imports: [FormsModule, Icon, DatePipe, Dialog, RowMenu],
  template: `
    @if (requests().length) {
      <section class="card mb-4 overflow-hidden">
        <div class="border-b border-line px-5 py-4"><h2 class="text-lg font-semibold">בקשות לשינוי שם ({{ requests().length }})</h2></div>
        <div class="grid gap-2 p-3 sm:p-5">
          @for (r of requests(); track r.id) {
            <div class="flex flex-wrap items-center gap-x-3 gap-y-2 rounded-xl bg-bg px-3 py-2">
              <span>מ-<b>{{ r.oldName }}</b> ל-<b>{{ r.newName }}</b></span>
              <span class="text-sm text-muted">{{ r.city }} · {{ r.by }} · {{ r.requestedAt | date: 'dd.MM HH:mm' }}</span>
              <span class="ms-auto flex gap-2">
                <button type="button" class="btn-primary h-10 px-4" (click)="decide(r, 'approve')">אישור</button>
                <button type="button" class="btn-secondary h-10 px-4" (click)="decide(r, 'reject')">דחייה</button>
              </span>
            </div>
          }
        </div>
      </section>
    }
    <section class="card overflow-hidden">
      <div class="border-b border-line px-5 py-4"><h1 class="text-xl font-semibold">קהילות</h1></div>
      <div class="grid gap-3 border-b border-line p-5 sm:grid-cols-2 sm:items-start">
        <div class="field">
          <label class="label" for="sc-search">מה לחפש?</label>
          <div class="relative">
            <span class="pointer-events-none absolute inset-y-0 start-3 grid place-items-center text-muted"><app-icon name="search" [size]="18" /></span>
            <input id="sc-search" class="input ps-10" placeholder="חיפוש ברשימה" [ngModel]="q()" (ngModelChange)="q.set($event)" />
          </div>
        </div>
        <form class="field" (ngSubmit)="add()">
          <label class="label" for="sc-new">איזו קהילה להוסיף?</label>
          <div class="flex gap-2">
            <input id="sc-new" name="scNew" class="input" maxlength="60" [class.invalid]="addError()" [(ngModel)]="newName" (input)="addError.set('')" />
            <button class="btn-primary shrink-0" type="submit"><app-icon name="plus" [size]="18" /> הוספה</button>
          </div>
          <div class="error-text">{{ addError() }}</div>
        </form>
      </div>
      @if (notice()) { <p class="notice mx-5 mt-4" role="status">{{ notice() }}</p> }
      <p class="px-5 pt-4 text-sm text-muted">מוצגות {{ shown().length }} מתוך {{ items().length }}. שינוי שם כאן חל מיד על כל הערים.</p>
      <div class="grid gap-x-3 gap-y-1 p-3 sm:grid-cols-2 sm:p-5 lg:grid-cols-3">
        @for (c of shown(); track c.id) {
          <div class="relative rounded-lg px-2 py-2 pe-12 odd:bg-bg">
            @if (editingId() === c.id) {
              <div class="grid gap-1">
                <input class="input h-10" maxlength="60" [attr.aria-label]="'שם חדש ל' + c.name" [(ngModel)]="editName" (input)="editError.set('')"
                  [class.invalid]="editError()" (keydown.enter)="save(c)" (keydown.escape)="editingId.set(null)" />
                <div class="flex gap-1.5">
                  <button type="button" class="btn-primary h-9 px-3 text-sm" (click)="save(c)">שמירה</button>
                  <button type="button" class="btn-secondary h-9 px-3 text-sm" (click)="editingId.set(null)">ביטול</button>
                </div>
                @if (editError()) { <span class="error-text">{{ editError() }}</span> }
              </div>
            } @else {
              <span class="font-medium" [innerHTML]="mark(c.name)"></span>
              <span class="block text-xs text-muted">{{ c.cities ? 'נבחרה ב-' + c.cities + (c.cities === 1 ? ' עיר' : ' ערים') : 'לא נבחרה עדיין' }}</span>
              <app-row-menu class="absolute end-1 top-1" [label]="'פעולות ל' + c.name">
                <button type="button" (click)="startEdit(c)">עריכה</button>
                <button type="button" class="text-bad" (click)="toDelete.set(c)">מחיקה</button>
              </app-row-menu>
            }
          </div>
        }
      </div>
    </section>

    <app-dialog [open]="!!toDelete()" title="מחיקה מהרשימה הכללית" [width]="480" (closed)="toDelete.set(null)">
      @if (toDelete(); as d) {
        <p>למחוק את "{{ d.name }}" מהרשימה הכללית?</p>
        <p class="mt-2 text-sm text-muted">
          @if (d.cities) { היא נבחרה ב-{{ d.cities }} {{ d.cities === 1 ? 'עיר' : 'ערים' }}, ותצא גם מהרשימה שלהן. עיר שכבר יש בה פניות עם הקהילה הזאת תשאיר אותה כקהילה ידנית, כדי שההיסטוריה לא תאבד. }
          @else { אף עיר לא בחרה בה עדיין. }
        </p>
        @if (deleteError()) { <p class="notice bad mt-3" role="alert">{{ deleteError() }}</p> }
        <div class="mt-5 flex gap-2">
          <button type="button" class="btn-primary !bg-bad" (click)="remove(d)">מחיקה</button>
          <button type="button" class="btn-secondary" (click)="toDelete.set(null)">ביטול</button>
        </div>
      }
    </app-dialog>
  `,
})
export class CatalogPage implements OnInit {
  private readonly api = inject(Api);
  protected readonly items = signal<CatalogRow[]>([]);
  protected readonly requests = signal<RenameRequest[]>([]);
  protected readonly q = signal('');
  protected readonly notice = signal('');
  protected readonly addError = signal('');
  protected readonly editError = signal('');
  protected readonly editingId = signal<number | null>(null);
  protected readonly toDelete = signal<CatalogRow | null>(null);
  protected readonly deleteError = signal('');
  protected readonly shown = computed(() => {
    const q = this.q().trim();
    return q ? this.items().filter((c) => c.name.includes(q)) : this.items();
  });

  protected async remove(c: CatalogRow) {
    this.deleteError.set('');
    try {
      await this.api.delete(`/api/admin/catalog/${c.id}`);
      this.toDelete.set(null);
      this.notice.set(`"${c.name}" נמחקה מהרשימה הכללית`);
      await this.load();
    } catch (e) {
      this.deleteError.set(errorMessage(e));
    }
  }
  protected newName = '';
  protected editName = '';

  async ngOnInit() {
    await this.load();
  }
  private async load() {
    const r = await this.api.get<{ items: CatalogRow[]; requests: RenameRequest[] }>('/api/admin/catalog');
    this.items.set(r.items);
    this.requests.set(r.requests);
  }
  protected mark(name: string) {
    return markHtml(name, this.q());
  }
  protected async add() {
    if (!this.newName.trim()) return this.addError.set('צריך למלא שם');
    try {
      await this.api.post('/api/admin/catalog', { name: this.newName });
      this.notice.set(`"${this.newName.trim()}" נוספה לרשימה הכללית`);
      this.newName = '';
      await this.load();
    } catch (e) {
      this.addError.set(errorMessage(e));
    }
  }
  protected startEdit(c: CatalogRow) {
    this.editName = c.name;
    this.editError.set('');
    this.editingId.set(c.id);
  }
  protected async save(c: CatalogRow) {
    if (!this.editName.trim()) return this.editError.set('צריך למלא שם');
    try {
      await this.api.put(`/api/admin/catalog/${c.id}`, { name: this.editName });
      this.editingId.set(null);
      this.notice.set(`השם שונה ל"${this.editName.trim()}" בכל הערים`);
      await this.load();
    } catch (e) {
      this.editError.set(errorMessage(e));
    }
  }
  protected async decide(r: RenameRequest, decision: 'approve' | 'reject') {
    try {
      await this.api.post(`/api/admin/catalog/requests/${r.id}/${decision}`);
      this.notice.set(decision === 'approve' ? `השם שונה ל"${r.newName}" בכל הערים` : `הבקשה לשנות את "${r.oldName}" נדחתה`);
    } catch (e) {
      this.notice.set(errorMessage(e));
    }
    await this.load();
  }
}
