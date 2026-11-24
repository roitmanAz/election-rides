import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Eye } from '../shared/eye';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthService, errorMessage } from '../core/auth.service';

const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

@Component({
  selector: 'app-login',
  imports: [FormsModule, Eye],
  template: `
    <main class="grid min-h-screen place-items-center p-4">
      <div class="card w-full max-w-md p-6 sm:p-8">
        <h1 class="text-2xl font-semibold">מטה רכב</h1>

        @if (step() === 'credentials') {
          <p class="mt-1 text-muted">כניסה לצוות המטה</p>
          @if (expired) {
            <p class="notice mt-4">החיבור נסגר אחרי זמן בלי פעילות. אפשר להיכנס שוב.</p>
          }
          @if (passwordSet) {
            <p class="notice mt-4">הסיסמה נשמרה. אפשר להיכנס.</p>
          }
          <form class="mt-6 grid gap-4" (ngSubmit)="submitCredentials()" novalidate>
            <div class="field">
              <label class="label" for="email">מה האימייל שלך?<span class="req">*</span></label>
              <input id="email" name="email" type="email" class="input" dir="ltr" autocomplete="username"
                [class.invalid]="emailError()" [(ngModel)]="email" (blur)="checkEmail()" />
              <div class="error-text">{{ emailError() }}</div>
            </div>
            <div class="field">
              <label class="label" for="password">מה הסיסמה?<span class="req">*</span></label>
              <div class="relative">
                <input id="password" name="password" [type]="eye.shown() ? 'text' : 'password'" class="input pl-12" dir="ltr" autocomplete="current-password"
                  [class.invalid]="passwordError()" [(ngModel)]="password" (blur)="checkPassword()" />
                <app-eye #eye />
              </div>
              <div class="error-text">{{ passwordError() }}</div>
            </div>
            @if (error()) {
              <p class="notice bad" role="alert">{{ error() }}</p>
            }
            <button class="btn-primary w-full" type="submit" [disabled]="busy()">כניסה</button>
          </form>
        }

        @if (step() === 'code') {
          <p class="mt-1 text-muted">
            שלחנו קוד בן 6 ספרות אל <span dir="ltr" class="font-medium text-ink">{{ sentTo() }}</span>
          </p>
          <form class="mt-6 grid gap-4" (ngSubmit)="submitCode()" novalidate>
            <div class="field">
              <label class="label" for="code">מה הקוד שקיבלת במייל?<span class="req">*</span></label>
              <input id="code" name="code" class="input text-center text-2xl tracking-[0.4em]" dir="ltr"
                inputmode="numeric" autocomplete="one-time-code" maxlength="6"
                [class.invalid]="codeError()" [(ngModel)]="code" (blur)="checkCode()" />
              <div class="error-text">{{ codeError() }}</div>
            </div>
            @if (error()) {
              <p class="notice bad" role="alert">{{ error() }}</p>
            }
            <button class="btn-primary w-full" type="submit" [disabled]="busy()">אישור</button>
            <div class="flex flex-wrap items-center justify-between gap-2">
              <button class="btn-link" type="button" (click)="resend()" [disabled]="busy()">שלחו לי קוד חדש</button>
              <button class="btn-link" type="button" (click)="back()">חזרה</button>
            </div>
            @if (info()) {
              <p class="notice">{{ info() }}</p>
            }
          </form>
        }
      </div>
    </main>
  `,
})
export class LoginPage {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly params = inject(ActivatedRoute).snapshot.queryParamMap;

  protected readonly expired = this.params.has('expired');
  protected readonly passwordSet = this.params.has('password-set');

  protected email = this.params.get('email') ?? '';
  protected password = '';
  protected code = '';

  protected readonly step = signal<'credentials' | 'code'>('credentials');
  protected readonly busy = signal(false);
  protected readonly error = signal('');
  protected readonly info = signal('');
  protected readonly emailError = signal('');
  protected readonly passwordError = signal('');
  protected readonly codeError = signal('');
  protected readonly sentTo = signal('');
  private challengeId = '';

  protected checkEmail(): boolean {
    const v = this.email.trim();
    this.emailError.set(!v ? 'צריך למלא אימייל' : EMAIL.test(v) ? '' : 'האימייל לא תקין');
    return !this.emailError();
  }

  protected checkPassword(): boolean {
    this.passwordError.set(this.password ? '' : 'צריך למלא סיסמה');
    return !this.passwordError();
  }

  protected checkCode(): boolean {
    const v = this.code.trim();
    this.codeError.set(/^\d{6}$/.test(v) ? '' : 'הקוד הוא 6 ספרות');
    return !this.codeError();
  }

  protected async submitCredentials() {
    const ok = [this.checkEmail(), this.checkPassword()].every(Boolean);
    if (!ok) return;
    await this.run(async () => {
      const r = await this.auth.login(this.email.trim(), this.password);
      if (r.status === 'signed-in') {
        await this.router.navigateByUrl('/');
      } else {
        this.challengeId = r.challengeId;
        this.sentTo.set(r.email);
        this.password = '';
        this.step.set('code');
      }
    });
  }

  protected async submitCode() {
    if (!this.checkCode()) return;
    await this.run(async () => {
      await this.auth.verify(this.challengeId, this.code.trim());
      await this.router.navigateByUrl('/');
    });
  }

  protected async resend() {
    await this.run(async () => {
      const r = await this.auth.resend(this.challengeId);
      this.challengeId = r.challengeId;
      this.sentTo.set(r.email);
      this.code = '';
      this.info.set('נשלח קוד חדש. הקוד הקודם כבר לא תקף.');
    });
  }

  protected back() {
    this.step.set('credentials');
    this.code = '';
    this.error.set('');
    this.info.set('');
  }

  private async run(action: () => Promise<void>) {
    this.busy.set(true);
    this.error.set('');
    this.info.set('');
    try {
      await action();
    } catch (e) {
      this.error.set(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}
