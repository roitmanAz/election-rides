import { Component, inject, input } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService, ROLE_NAMES } from '../core/auth.service';
import { Icon, IconName } from './icon';

export interface ShellTab {
  path: string;
  label: string;
  icon: IconName;
}

/**
 * Screen frame for the admin screens (spec 3.1, 3.2): a white top bar, a fixed side menu with an icon per
 * screen and its own link, and on a phone the menu turns into icon tiles.
 */
@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, Icon],
  template: `
    @if (auth.me(); as me) {
      <div class="min-h-screen">
        <header class="flex flex-wrap items-center gap-x-4 gap-y-1 border-b border-line bg-surface px-4 py-3 sm:px-6">
          <span class="text-xl font-semibold">מטה הסעות</span>
          <span class="text-muted">{{ subtitle() }}</span>
          <span class="ms-auto text-muted">{{ me.fullName }} · {{ roles[me.role] }}</span>
          <button type="button" class="btn-secondary h-10 px-3" (click)="logout()">
            <app-icon name="logout" [size]="18" /> יציאה
          </button>
        </header>
        <div class="mx-auto grid max-w-[1400px] gap-4 p-3 sm:p-4 lg:grid-cols-[220px_minmax(0,1fr)]">
          <nav [attr.aria-label]="menuLabel()" class="grid grid-cols-3 gap-2 sm:grid-cols-6 lg:flex lg:flex-col lg:self-start">
            @for (t of tabs(); track t.path) {
              <a [routerLink]="t.path" routerLinkActive="!bg-ink !text-white"
                class="flex flex-col items-center gap-1 rounded-2xl bg-surface px-2 py-2.5 text-center text-sm font-medium text-ink shadow-[var(--shadow-card)] hover:bg-soft lg:flex-row lg:gap-3 lg:rounded-full lg:px-4 lg:text-base lg:shadow-none lg:bg-transparent">
                <app-icon [name]="t.icon" />{{ t.label }}
              </a>
            }
          </nav>
          <main class="min-w-0"><router-outlet /></main>
        </div>
      </div>
    }
  `,
})
export class Shell {
  protected readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  protected readonly roles = ROLE_NAMES;
  readonly tabs = input.required<ShellTab[]>();
  readonly subtitle = input('');
  readonly menuLabel = input('תפריט');

  protected async logout() {
    await this.auth.logout();
    await this.router.navigateByUrl('/login');
  }
}
