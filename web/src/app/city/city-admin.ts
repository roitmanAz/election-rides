import { Component, inject } from '@angular/core';
import { AuthService } from '../core/auth.service';
import { Shell, ShellTab } from '../shared/shell';

/**
 * City admin screen (spec 3.1). The voter base joins the menu in stage 7.
 */
@Component({
  selector: 'app-city-admin',
  imports: [Shell],
  template: `<app-shell [tabs]="tabs" [subtitle]="'מנהל עיר · ' + (auth.me()?.cityName ?? '')" menuLabel="תפריט מנהל העיר" />`,
})
export class CityAdmin {
  protected readonly auth = inject(AuthService);
  protected readonly tabs: ShellTab[] = [
    { path: 'dashboard', label: 'דשבורד', icon: 'chart' },
    { path: 'clusters', label: 'אשכולות', icon: 'clusters' },
    { path: 'communities', label: 'קהילות', icon: 'comms' },
    { path: 'streets', label: 'רחובות', icon: 'streets' },
    { path: 'users', label: 'משתמשים', icon: 'users' },
    { path: 'settings', label: 'הגדרות העיר', icon: 'city' },
  ];
}
