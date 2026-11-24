import { Component, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Api } from '../core/api';
import { errorMessage } from '../core/auth.service';
import { Combo, ComboItem } from '../shared/combo';
import { Dialog } from '../shared/dialog';
import { Icon } from '../shared/icon';
import { Shell, ShellTab } from '../shared/shell';
import { RowMenu } from '../shared/row-menu';

/** System admin screen (spec 3.2). Opens on the live status. */
@Component({
  selector: 'app-system-admin',
  imports: [Shell],
  template: `<app-shell area="SystemAdmin" [tabs]="tabs" subtitle="מנהל מערכת" menuLabel="תפריט מנהל המערכת" />`,
})
export class SystemAdmin {
  protected readonly tabs: ShellTab[] = [
    { path: 'live', label: 'תמונת מצב', icon: 'pulse' },
    { path: 'cities', label: 'ערים', icon: 'city' },
    { path: 'operations', label: 'מבצעי בחירות', icon: 'calendar' },
    { path: 'reports', label: 'דוחות', icon: 'chart' },
    { path: 'certificates', label: 'אישורי מחיקה', icon: 'trash' },
    { path: 'audit', label: 'יומן פעולות', icon: 'list' },
    { path: 'sysadmins', label: 'מנהלי מערכת', icon: 'users' },
    { path: 'security', label: 'מדיניות אבטחה', icon: 'lock' },
    { path: 'party', label: 'פרטיות והגדרות', icon: 'shield' },
  ];
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
  imports: [FormsModule, Dialog, Icon, Combo, DatePipe, RowMenu],
  template: `
    <section class="card overflow-hidden">
      <div class="flex flex-wrap items-center gap-3 border-b border-line px-5 py-4">
        <h1 class="text-xl font-semibold">ערים</h1>
        <button type="button" class="btn-primary ms-auto" (click)="openNew()"><app-icon name="plus" [size]="18" /> עיר חדשה</button>
      </div>
      @if (notice()) { <p class="notice mx-5 mt-4" role="status">{{ notice() }}</p> }
      <div class="grid gap-3 p-3 sm:p-5">
        @for (c of cities(); track c.id) {
          <article class="grid gap-3 rounded-2xl bg-bg p-4 md:grid-cols-[minmax(0,1fr)_minmax(0,1.4fr)_auto]" [class.opacity-70]="!c.isActive">
            <div class="flex items-start gap-2 md:contents">
              <div class="min-w-0">
                <h2 class="text-lg font-semibold">{{ c.name }} @if (!c.isActive) { <span class="text-sm font-normal text-bad">(מושבתת)</span> }</h2>
                @for (a of c.admins; track a.email) {
                  <p class="flex flex-wrap gap-x-2 text-sm"><span>{{ a.fullName }}</span><span class="text-muted" dir="ltr">{{ a.email }}</span>
                    @if (a.invited) { <span class="text-warn">הוזמן, עוד לא נכנס</span> }
                    @else if (a.lastLoginAt) { <span class="text-muted">נכנס {{ a.lastLoginAt | date: 'dd.MM.yy HH:mm' }}</span> }
                  </p>
              } @empty {
                  <p class="inline-flex items-center gap-1 text-sm font-medium text-bad"><app-icon name="warn" [size]="16" /> אין מנהל עיר</p>
                }
              </div>
              <app-row-menu class="relative ms-auto md:order-last md:self-start" [label]="'פעולות ל' + c.name">
                <button type="button" (click)="manage(c)">ניהול</button>
                <button type="button" [class.text-bad]="c.isActive" (click)="toToggle.set(c)">{{ c.isActive ? 'השבתה' : 'הפעלה' }}</button>
              </app-row-menu>
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

    <app-dialog [open]="!!managed()" [title]="'ניהול ' + (managed()?.name ?? '')" [width]="560" (closed)="managed.set(null)">
      @if (managed(); as m) {
        <div class="grid gap-4">
          <section>
            <h3 class="mb-2 font-semibold">מנהלי העיר</h3>
            @for (a of admins(); track a.id) {
              <div class="flex flex-wrap items-center gap-2 rounded-xl px-3 py-2 text-sm odd:bg-bg">
                <b>{{ a.fullName }}</b><span class="text-muted" dir="ltr">{{ a.email }}</span>
                @if (a.invited) { <span class="text-warn">הוזמן</span> }
                @if (confirmRemove() === a.id) {
                  <span class="ms-auto">להסיר?</span>
                  <button type="button" class="btn-primary h-9 !bg-bad px-3 text-sm" (click)="removeAdmin(a.id)">כן, הסר</button>
                  <button type="button" class="btn-secondary h-9 px-3 text-sm" (click)="confirmRemove.set(null)">לא</button>
                } @else {
                  <button type="button" class="btn-secondary ms-auto h-9 px-3 text-sm text-bad" (click)="confirmRemove.set(a.id)">הסר</button>
                }
              </div>
            } @empty { <p class="text-sm font-medium text-bad">אין מנהל עיר</p> }
            <div class="mt-3 grid gap-2 sm:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_auto] sm:items-end">
              <label class="field"><span class="label">מה השם של המנהל החדש?<span class="req">*</span></span><input class="input" [(ngModel)]="newAdminName" /></label>
              <label class="field"><span class="label">מה האימייל שלו?<span class="req">*</span></span><input class="input" type="email" dir="ltr" [(ngModel)]="newAdminEmail" /></label>
              <button type="button" class="btn-primary" (click)="addAdmin()">הזמנה</button>
            </div>
          </section>
          @if (manageMsg()) { <p class="notice" [class.bad]="manageBad()" role="status">{{ manageMsg() }}</p> }
        </div>
      }
    </app-dialog>

    <app-dialog [open]="!!toToggle()" [title]="(toToggle()?.isActive ? 'השבתת ' : 'הפעלת ') + (toToggle()?.name ?? '')" [width]="460" (closed)="toToggle.set(null)">
      @if (toToggle(); as t) {
        <p>{{ t.isActive ? 'הנתונים נשמרים, אבל משתמשי העיר לא יוכלו להיכנס עד שהעיר תופעל שוב.' : 'משתמשי העיר יוכלו להיכנס שוב.' }}</p>
        @if (toggleError()) { <p class="notice bad mt-3" role="alert">{{ toggleError() }}</p> }
        <div class="mt-5 flex gap-2">
          <button type="button" class="btn-primary" [class.!bg-bad]="t.isActive" (click)="setActive(t, !t.isActive)">{{ t.isActive ? 'השבתה' : 'הפעלה' }}</button>
          <button type="button" class="btn-secondary" (click)="toToggle.set(null)">ביטול</button>
        </div>
      }
    </app-dialog>

    <app-dialog [open]="newOpen()" title="עיר חדשה" [width]="520" (closed)="newOpen.set(false)">
      <form class="grid gap-3" (ngSubmit)="create()" novalidate>
        <div class="field">
          <label class="label" for="loc">איזו עיר?<span class="req">*</span></label>
          <app-combo inputId="loc" [items]="localities()" [(value)]="localityCode" [invalid]="!!locErr()"
            placeholder="מקלידים לפחות 2 אותיות" [minChars]="2" emptyText="לא נמצא ברשימת היישובים" (typed)="searchLocalities($event)" (left)="checkLoc()" />
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
  protected readonly managed = signal<CityRow | null>(null);
  protected readonly admins = signal<{ id: string; fullName: string; email: string; invited: boolean }[]>([]);
  protected readonly confirmRemove = signal<string | null>(null);
  protected readonly manageMsg = signal('');
  protected readonly manageBad = signal(false);
  protected newAdminName = '';
  protected newAdminEmail = '';

  protected async manage(c: CityRow) {
    this.newAdminName = this.newAdminEmail = '';
    this.manageMsg.set('');
    this.confirmRemove.set(null);
    this.admins.set(await this.api.get(`/api/admin/cities/${c.id}/admins`));
    this.managed.set(c);
  }

  private async afterManage(msg: string, bad = false) {
    this.manageBad.set(bad);
    this.manageMsg.set(msg);
    if (bad) return;
    const id = this.managed()!.id;
    await this.load();
    this.admins.set(await this.api.get(`/api/admin/cities/${id}/admins`));
    this.managed.set(this.cities().find((c) => c.id === id) ?? null);
  }

  protected async addAdmin() {
    if (!this.newAdminName.trim()) return this.afterManage('צריך למלא שם', true);
    if (!EMAIL.test(this.newAdminEmail.trim())) return this.afterManage('האימייל לא תקין', true);
    try {
      await this.api.post(`/api/admin/cities/${this.managed()!.id}/admins`, { email: this.newAdminEmail, fullName: this.newAdminName });
      const email = this.newAdminEmail.trim();
      this.newAdminName = this.newAdminEmail = '';
      await this.afterManage(`נשלחה הזמנה אל ${email}`);
    } catch (e) {
      await this.afterManage(errorMessage(e), true);
    }
  }

  protected async removeAdmin(userId: string) {
    try {
      await this.api.delete(`/api/admin/cities/${this.managed()!.id}/admins/${userId}`);
      this.confirmRemove.set(null);
      await this.afterManage('מנהל העיר הוסר');
    } catch (e) {
      await this.afterManage(errorMessage(e), true);
    }
  }

  protected readonly toToggle = signal<CityRow | null>(null);
  protected readonly toggleError = signal('');

  protected async setActive(c: CityRow, active: boolean) {
    this.toggleError.set('');
    try {
      await this.api.post(`/api/admin/cities/${c.id}/active`, { active });
      this.toToggle.set(null);
      this.notice.set(active ? `העיר ${c.name} הופעלה` : `העיר ${c.name} הושבתה`);
      await this.load();
    } catch (e) {
      this.toggleError.set(errorMessage(e));
    }
  }

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

interface Operation {
  id: string;
  type: 'Knesset' | 'Municipal';
  electionDay: string;
  isActive: boolean;
  closedAt: string | null;
  cities: { id: string; name: string }[];
}

/** Election operations (spec 3.2): one active at a time; open a new one with type, day and cities; close the active one. */
@Component({
  selector: 'app-operations',
  imports: [FormsModule, Dialog, Icon, DatePipe],
  template: `
    <section class="card overflow-hidden">
      <div class="flex flex-wrap items-center gap-3 border-b border-line px-5 py-4">
        <h1 class="text-xl font-semibold">מבצעי בחירות</h1>
        <button type="button" class="btn-primary ms-auto" [disabled]="hasActive()" (click)="openNew()"><app-icon name="plus" [size]="18" /> מבצע חדש</button>
      </div>
      @if (hasActive()) { <p class="mx-5 mt-4 text-sm text-muted">רק מבצע אחד פעיל בכל פעם.</p> }
      <div class="grid gap-3 p-3 sm:p-5">
        @for (o of ops(); track o.id) {
          <article class="grid gap-1 rounded-2xl bg-bg p-4">
            <h2 class="flex flex-wrap items-center gap-2 text-lg font-semibold">
              {{ o.type === 'Knesset' ? 'בחירות לכנסת' : 'בחירות לרשויות המקומיות' }} · {{ o.electionDay | date: 'dd.MM.yyyy' }}
              @if (o.isActive) { <span class="rounded-full bg-action-soft px-2.5 py-0.5 text-sm font-medium">פעיל</span> }
              @else { <span class="rounded-full bg-surface px-2.5 py-0.5 text-sm text-muted">נסגר</span> }
            </h2>
            <p class="text-muted">{{ o.cities.length }} ערים: {{ cityNames(o) }}</p>
            @if (o.isActive) { <div><button type="button" class="btn-secondary mt-1 h-9 px-3 text-sm text-bad" (click)="openClose(o)">סגירת המבצע</button></div> }
            @else if (o.closedAt) { <p class="text-sm text-muted">נסגר {{ o.closedAt | date: 'dd.MM.yyyy HH:mm' }}</p> }
          </article>
        } @empty {
          <p class="px-3 py-6 text-center text-muted">עוד אין מבצעים. ערים מקבלות פניות רק בתוך מבצע פעיל.</p>
        }
      </div>
    </section>

    <app-dialog [open]="!!toClose()" title="סגירת המבצע" [width]="480" (closed)="toClose.set(null)">
      <p>אחרי הסגירה הערים לא יקבלו פניות חדשות, ואי אפשר לפתוח את המבצע מחדש.</p>
      <p class="mt-2 text-sm text-muted">{{ deletionText() }}</p>
      @if (closeError()) { <p class="notice bad mt-3" role="alert">{{ closeError() }}</p> }
      <div class="mt-5 flex gap-2">
        <button type="button" class="btn-primary !bg-bad" [disabled]="busy()" (click)="close()">סגירה</button>
        <button type="button" class="btn-secondary" (click)="toClose.set(null)">ביטול</button>
      </div>
    </app-dialog>

    <app-dialog [open]="newOpen()" title="מבצע חדש" [width]="520" (closed)="newOpen.set(false)">
      <div class="grid gap-3">
        <fieldset class="field">
          <legend class="label mb-1.5">איזה סוג בחירות?<span class="req">*</span></legend>
          <div class="flex w-max gap-1 rounded-xl bg-soft p-1">
            <button type="button" class="h-10 rounded-[10px] px-4 font-medium" [class]="type === 'Knesset' ? 'bg-ink text-white' : ''" (click)="type = 'Knesset'">כנסת</button>
            <button type="button" class="h-10 rounded-[10px] px-4 font-medium" [class]="type === 'Municipal' ? 'bg-ink text-white' : ''" (click)="type = 'Municipal'">רשויות מקומיות</button>
          </div>
        </fieldset>
        <label class="field"><span class="label">מתי יום הבחירות?<span class="req">*</span></span><input type="date" class="input w-48" [(ngModel)]="day" /></label>
        <fieldset class="field">
          <legend class="label mb-1.5">אילו ערים משתתפות?<span class="req">*</span></legend>
          <div class="grid gap-1.5 sm:grid-cols-2">
            @for (c of cities(); track c.id) {
              <label class="flex items-center gap-2"><input type="checkbox" class="size-4" [checked]="chosen.has(c.id)" (change)="toggle(c.id)" /> {{ c.name }}</label>
            }
          </div>
        </fieldset>
        @if (error()) { <p class="notice bad" role="alert">{{ error() }}</p> }
        <div class="flex gap-2">
          <button type="button" class="btn-primary" [disabled]="busy()" (click)="create()">פתיחת המבצע</button>
          <button type="button" class="btn-secondary" (click)="newOpen.set(false)">ביטול</button>
        </div>
      </div>
    </app-dialog>
  `,
})
export class OperationsPage implements OnInit {
  private readonly api = inject(Api);
  protected readonly ops = signal<Operation[]>([]);
  protected readonly cities = signal<{ id: string; name: string }[]>([]);
  protected readonly newOpen = signal(false);
  protected readonly busy = signal(false);
  protected readonly error = signal('');
  protected type: 'Knesset' | 'Municipal' | null = null;
  protected day = '';
  protected chosen = new Set<string>();
  protected readonly toClose = signal<Operation | null>(null);
  protected readonly closeError = signal('');
  protected readonly deletionText = signal('');

  protected async openClose(o: Operation) {
    this.closeError.set('');
    const p = await this.api.get<{ baseDeletionDays: number | null; deletionDate: string | null }>('/api/admin/party');
    this.deletionText.set(p.baseDeletionDays
      ? `הבייסים יימחקו אוטומטית ${p.baseDeletionDays} ימים אחרי יום הבחירות${p.deletionDate ? ` (${p.deletionDate.split('-').reverse().join('.')})` : ''}.`
      : 'עוד לא נקבע מתי נמחקים הבייסים. אפשר לקבוע במסך "פרטיות והגדרות".');
    this.toClose.set(o);
  }

  protected async close() {
    this.busy.set(true);
    try {
      await this.api.post(`/api/admin/operations/${this.toClose()!.id}/close`);
      this.toClose.set(null);
      await this.load();
    } catch (e) {
      this.closeError.set(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  async ngOnInit() {
    await this.load();
  }

  private async load() {
    const [ops, cities] = await Promise.all([
      this.api.get<Operation[]>('/api/admin/operations'),
      this.api.get<{ id: string; name: string }[]>('/api/admin/cities'),
    ]);
    this.ops.set(ops);
    this.cities.set(cities);
  }

  protected hasActive() {
    return this.ops().some((o) => o.isActive);
  }

  protected cityNames(o: Operation) {
    return o.cities.map((c) => c.name).join(', ');
  }

  protected openNew() {
    this.type = null;
    this.day = '';
    this.chosen = new Set();
    this.error.set('');
    this.newOpen.set(true);
  }

  protected toggle(id: string) {
    this.chosen.has(id) ? this.chosen.delete(id) : this.chosen.add(id);
  }

  protected async create() {
    if (!this.type) return this.error.set('צריך לבחור סוג בחירות');
    if (!this.day) return this.error.set('צריך לבחור את יום הבחירות');
    if (!this.chosen.size) return this.error.set('צריך לבחור לפחות עיר אחת');
    this.busy.set(true);
    this.error.set('');
    try {
      await this.api.post('/api/admin/operations', { type: this.type, electionDay: this.day, cityIds: [...this.chosen] });
      this.newOpen.set(false);
      await this.load();
    } catch (e) {
      this.error.set(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}
