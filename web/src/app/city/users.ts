import { Component, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Api } from '../core/api';
import { AuthService, ROLE_NAMES, Role, errorMessage } from '../core/auth.service';
import { Dialog } from '../shared/dialog';
import { Icon } from '../shared/icon';

interface User {
  id: string;
  fullName: string;
  email: string;
  role: Role;
  lastLoginAt: string | null;
  invited: boolean;
  lockedUntil: string | null;
}

const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

/** City users (spec 3.1): name, email, role, last sign-in, remove with confirmation; invite by email. */
@Component({
  selector: 'app-users',
  imports: [FormsModule, Dialog, Icon, DatePipe],
  template: `
    <section class="card overflow-hidden">
      <div class="flex flex-wrap items-center gap-3 border-b border-line px-5 py-4">
        <h1 class="text-xl font-semibold">משתמשים</h1>
        <button type="button" class="btn-primary ms-auto" (click)="openInvite()"><app-icon name="plus" [size]="18" /> משתמש חדש</button>
      </div>
      @if (notice()) { <p class="notice mx-5 mt-4" role="status">{{ notice() }}</p> }
      <div class="p-3 sm:p-5">
        <div class="hidden grid-cols-[minmax(0,1fr)_minmax(0,1.3fr)_110px_150px_auto] gap-3 px-3 pb-2 text-sm font-medium text-muted md:grid">
          <span>שם</span><span>אימייל</span><span>תפקיד</span><span>כניסה אחרונה</span><span></span>
        </div>
        @for (u of users(); track u.id) {
          <div class="grid items-center gap-x-3 gap-y-1 rounded-xl px-3 py-2.5 odd:bg-bg md:grid-cols-[minmax(0,1fr)_minmax(0,1.3fr)_110px_150px_auto]">
            <span class="font-medium">{{ u.fullName }}</span>
            <span class="min-w-0 truncate text-muted" dir="ltr" style="text-align: right">{{ u.email }}</span>
            <span>{{ roles[u.role] }}</span>
            <span class="text-sm">
              @if (u.lockedUntil) {
                <span class="inline-flex items-center gap-1 rounded-full bg-bad-soft px-2.5 py-0.5 text-bad"><app-icon name="lock" [size]="14" /> נעול עד {{ u.lockedUntil | date: 'HH:mm' }}</span>
              } @else if (u.invited) {
                <span class="rounded-full bg-warn-soft px-2.5 py-0.5 text-warn">הוזמן, עוד לא נכנס</span>
              } @else if (u.lastLoginAt) {
                <span class="tabular-nums text-muted">{{ u.lastLoginAt | date: 'dd.MM.yy HH:mm' }}</span>
              }
            </span>
            <span class="flex flex-wrap gap-2 md:justify-end">
              @if (u.lockedUntil) {
                <button type="button" class="btn-secondary h-10 px-3" (click)="unlock(u)"><app-icon name="lock" [size]="18" /> שחרר נעילה</button>
              }
              @if (u.invited) {
                <button type="button" class="btn-secondary h-10 px-3" (click)="resend(u)"><app-icon name="mail" [size]="18" /> שלח הזמנה שוב</button>
              }
              @if (u.id !== auth.me()?.id) {
                <button type="button" class="btn-secondary h-10 px-3 text-bad" (click)="toRemove.set(u)"><app-icon name="trash" [size]="18" /> הסר</button>
              }
            </span>
          </div>
        }
      </div>
    </section>

    <app-dialog [open]="inviteOpen()" title="משתמש חדש" [width]="480" (closed)="inviteOpen.set(false)">
      <form class="grid gap-3" (ngSubmit)="invite()" novalidate>
        <div class="field">
          <label class="label" for="u-name">מה השם המלא?<span class="req">*</span></label>
          <input id="u-name" name="name" class="input" [(ngModel)]="name" [class.invalid]="nameErr()" (blur)="checkName()" />
          <div class="error-text">{{ nameErr() }}</div>
        </div>
        <div class="field">
          <label class="label" for="u-email">מה האימייל?<span class="req">*</span></label>
          <input id="u-email" name="email" type="email" dir="ltr" class="input" [(ngModel)]="email" [class.invalid]="emailErr()" (blur)="checkEmail()" />
          <div class="error-text">{{ emailErr() }}</div>
        </div>
        <fieldset class="field">
          <legend class="label mb-1.5">מה התפקיד?<span class="req">*</span></legend>
          <div class="flex w-max flex-wrap gap-1 rounded-xl bg-soft p-1" [class.ring-2]="roleErr()" [class.ring-bad]="roleErr()">
            @for (r of cityRoles; track r) {
              <button type="button" class="h-10 rounded-[10px] px-4 font-medium" [class]="role === r ? 'bg-ink text-white' : ''" (click)="role = r; roleErr.set('')">{{ roles[r] }}</button>
            }
          </div>
          <div class="error-text">{{ roleErr() }}</div>
        </fieldset>
        <p class="text-sm text-muted">המשתמש יקבל מייל עם קישור לקביעת סיסמה.</p>
        @if (inviteError()) { <p class="notice bad" role="alert">{{ inviteError() }}</p> }
        <div class="flex gap-2">
          <button class="btn-primary" type="submit" [disabled]="busy()">שליחת הזמנה</button>
          <button class="btn-secondary" type="button" (click)="inviteOpen.set(false)">ביטול</button>
        </div>
      </form>
    </app-dialog>

    <app-dialog [open]="!!toRemove()" title="הסרת משתמש" [width]="440" (closed)="toRemove.set(null)">
      <p>להסיר את {{ toRemove()?.fullName }}? הוא לא יוכל להיכנס יותר, וההיסטוריה שלו נשמרת.</p>
      <div class="mt-5 flex gap-2">
        <button type="button" class="btn-primary !bg-bad" (click)="remove()">הסר</button>
        <button type="button" class="btn-secondary" (click)="toRemove.set(null)">ביטול</button>
      </div>
    </app-dialog>
  `,
})
export class UsersPage implements OnInit {
  private readonly api = inject(Api);
  protected readonly auth = inject(AuthService);
  protected readonly roles = ROLE_NAMES;
  protected readonly cityRoles: Role[] = ['Operator', 'Dispatcher', 'CityAdmin'];

  protected readonly users = signal<User[]>([]);
  protected readonly notice = signal('');
  protected readonly inviteOpen = signal(false);
  protected readonly toRemove = signal<User | null>(null);
  protected readonly busy = signal(false);
  protected readonly inviteError = signal('');
  protected readonly nameErr = signal('');
  protected readonly emailErr = signal('');
  protected readonly roleErr = signal('');
  protected name = '';
  protected email = '';
  protected role: Role | null = null;

  async ngOnInit() {
    await this.load();
  }

  private async load() {
    this.users.set(await this.api.get<User[]>('/api/city/users'));
  }

  protected openInvite() {
    this.name = this.email = '';
    this.role = null;
    [this.nameErr, this.emailErr, this.roleErr, this.inviteError].forEach((s) => s.set(''));
    this.inviteOpen.set(true);
  }

  protected checkName() {
    this.nameErr.set(this.name.trim() ? '' : 'צריך למלא שם');
    return !this.nameErr();
  }

  protected checkEmail() {
    const v = this.email.trim();
    this.emailErr.set(!v ? 'צריך למלא אימייל' : EMAIL.test(v) ? '' : 'האימייל לא תקין');
    return !this.emailErr();
  }

  protected async invite() {
    const roleOk = !!this.role;
    this.roleErr.set(roleOk ? '' : 'צריך לבחור תפקיד');
    if (![this.checkName(), this.checkEmail(), roleOk].every(Boolean)) return;
    this.busy.set(true);
    this.inviteError.set('');
    try {
      await this.api.post('/api/city/users', { email: this.email, fullName: this.name, role: this.role });
      this.inviteOpen.set(false);
      this.notice.set(`נשלחה הזמנה אל ${this.email.trim()}`);
      await this.load();
    } catch (e) {
      this.inviteError.set(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  protected async remove() {
    const u = this.toRemove();
    if (!u) return;
    await this.api.delete(`/api/city/users/${u.id}`);
    this.toRemove.set(null);
    await this.load();
  }

  protected async unlock(u: User) {
    await this.api.post(`/api/city/users/${u.id}/unlock`);
    this.notice.set(`הנעילה של ${u.fullName} שוחררה`);
    await this.load();
  }

  protected async resend(u: User) {
    await this.api.post(`/api/city/users/${u.id}/invite`);
    this.notice.set(`נשלחה הזמנה חדשה אל ${u.email}. הקישור הקודם כבר לא תקף.`);
  }
}
