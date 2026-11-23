import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthService, errorMessage } from '../core/auth.service';

export const MIN_PASSWORD = 10;

@Component({
  selector: 'app-set-password',
  imports: [FormsModule],
  template: `
    <main class="grid min-h-screen place-items-center p-4">
      <div class="card w-full max-w-md p-6 sm:p-8">
        <h1 class="text-2xl font-semibold">קביעת סיסמה</h1>
        <p class="mt-1 text-muted">הסיסמה צריכה להיות באורך {{ min }} תווים לפחות.</p>

        @if (!token) {
          <p class="notice bad mt-6">הקישור לא שלם. כדאי לפתוח אותו שוב מהמייל.</p>
        } @else {
          <form class="mt-6 grid gap-4" (ngSubmit)="submit()" novalidate>
            <div class="field">
              <label class="label" for="pw">מה הסיסמה החדשה?<span class="req">*</span></label>
              <input id="pw" name="pw" type="password" class="input" dir="ltr" autocomplete="new-password"
                [class.invalid]="pwError()" [(ngModel)]="pw" (blur)="checkPw()" />
              <div class="error-text">{{ pwError() }}</div>
            </div>
            <div class="field">
              <label class="label" for="pw2">אפשר להקליד אותה שוב?<span class="req">*</span></label>
              <input id="pw2" name="pw2" type="password" class="input" dir="ltr" autocomplete="new-password"
                [class.invalid]="pw2Error()" [(ngModel)]="pw2" (blur)="checkPw2()" />
              <div class="error-text">{{ pw2Error() }}</div>
            </div>
            @if (error()) {
              <p class="notice bad" role="alert">{{ error() }}</p>
            }
            <button class="btn-primary w-full" type="submit" [disabled]="busy()">שמירת הסיסמה</button>
          </form>
        }
      </div>
    </main>
  `,
})
export class SetPasswordPage {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  protected readonly token = inject(ActivatedRoute).snapshot.queryParamMap.get('token') ?? '';
  protected readonly min = MIN_PASSWORD;

  protected pw = '';
  protected pw2 = '';
  protected readonly pwError = signal('');
  protected readonly pw2Error = signal('');
  protected readonly error = signal('');
  protected readonly busy = signal(false);

  protected checkPw(): boolean {
    this.pwError.set(
      !this.pw ? 'צריך למלא סיסמה' : this.pw.length < MIN_PASSWORD ? `צריך לפחות ${MIN_PASSWORD} תווים` : '',
    );
    return !this.pwError();
  }

  protected checkPw2(): boolean {
    this.pw2Error.set(this.pw2 !== this.pw ? 'שתי הסיסמאות לא זהות' : '');
    return !this.pw2Error();
  }

  protected async submit() {
    if (![this.checkPw(), this.checkPw2()].every(Boolean)) return;
    this.busy.set(true);
    this.error.set('');
    try {
      const r = await this.auth.setPassword(this.token, this.pw);
      await this.router.navigate(['/login'], { queryParams: { 'password-set': 1, email: r.email } });
    } catch (e) {
      this.error.set(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}
