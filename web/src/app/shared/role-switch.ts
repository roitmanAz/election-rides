import { Component, computed, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthService, Role } from '../core/auth.service';

/** The city areas and the role that opens each one. */
export const AREAS: { role: Role; path: string; label: string }[] = [
  { role: 'CityAdmin', path: '/city', label: 'מעבר לניהול העיר' },
  { role: 'Dispatcher', path: '/dispatcher', label: 'מעבר לשיבוץ' },
  { role: 'Operator', path: '/operator', label: 'מעבר למוקד' },
];

/** For a user with several roles in the city: links to the other areas they may open. Shows nothing otherwise. */
@Component({
  selector: 'app-role-switch',
  imports: [RouterLink],
  host: { class: 'contents' },
  template: `
    @for (a of others(); track a.role) {
      <a [routerLink]="a.path" class="btn-secondary h-10 px-3 text-sm">{{ a.label }}</a>
    }
  `,
})
export class RoleSwitch {
  private readonly auth = inject(AuthService);
  readonly current = input.required<Role>();
  protected readonly others = computed(() => {
    const roles = this.auth.me()?.roles ?? [];
    return AREAS.filter((a) => a.role !== this.current() && roles.includes(a.role));
  });
}
