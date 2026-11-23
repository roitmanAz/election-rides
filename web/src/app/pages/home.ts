import { Component, inject } from '@angular/core';
import { Router } from '@angular/router';
import { AuthService, ROLE_NAMES } from '../core/auth.service';

/** Temporary landing page after sign-in, until the role screens are built (stages 3 to 8). */
@Component({
  selector: 'app-home',
  template: `
    @if (auth.me(); as me) {
      <main class="min-h-screen p-4 sm:p-8">
        <header class="card mx-auto flex max-w-3xl flex-wrap items-center gap-3 p-4 sm:px-6">
          <span class="text-xl font-semibold">מטה הסעות</span>
          @if (me.cityName) {
            <span class="text-muted">{{ me.cityName }}</span>
          }
          <span class="ms-auto text-muted">{{ me.fullName }} · {{ roleName(me.role) }}</span>
          <button class="btn-secondary h-10" type="button" (click)="logout()">יציאה</button>
        </header>
        <section class="card mx-auto mt-4 max-w-3xl p-6">
          <h1 class="text-xl font-semibold">נכנסת למערכת</h1>
          <p class="mt-1 text-muted">המסך של התפקיד שלך ייבנה בשלבים הבאים.</p>
        </section>
      </main>
    }
  `,
})
export class HomePage {
  protected readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  protected roleName = (r: keyof typeof ROLE_NAMES) => ROLE_NAMES[r];

  protected async logout() {
    await this.auth.logout();
    await this.router.navigateByUrl('/login');
  }
}
