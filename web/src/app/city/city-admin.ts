import { Component, inject } from '@angular/core';
import { AuthService } from '../core/auth.service';
import { Shell, ShellTab } from '../shared/shell';

/**
 * City admin screen (spec 3.1): dashboard, clusters, communities, streets, voter base, users, city settings.
 */
@Component({
  selector: 'app-city-admin',
  imports: [Shell],
  template: `<app-shell area="CityAdmin" [tabs]="tabs" [subtitle]="'מנהל עיר · ' + (auth.me()?.cityName ?? '')" menuLabel="תפריט מנהל העיר" />`,
})
export class CityAdmin {
  protected readonly auth = inject(AuthService);
  protected readonly tabs: ShellTab[] = [
    { path: 'dashboard', label: 'דשבורד', icon: 'chart' },
    { path: 'analytics', label: 'ניתוח נתונים', icon: 'pulse' },
    { path: 'clusters', label: 'אשכולות', icon: 'clusters' },
    { path: 'communities', label: 'קהילות', icon: 'comms' },
    { path: 'streets', label: 'רחובות', icon: 'streets' },
    { path: 'base', label: 'בייס מצביעים', icon: 'base' },
    { path: 'users', label: 'משתמשים', icon: 'users' },
    { path: 'settings', label: 'הגדרות העיר', icon: 'city' },
  ];
}
