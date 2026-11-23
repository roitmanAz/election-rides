import { Component, inject } from '@angular/core';
import { AuthService } from '../core/auth.service';
import { Shell, ShellTab } from '../shared/shell';

/**
 * City admin screen (spec 3.1). Dashboard and voter base join the menu in their stages (5 and 7).
 */
@Component({
  selector: 'app-city-admin',
  imports: [Shell],
  template: `<app-shell [tabs]="tabs" [subtitle]="'מנהל עיר · ' + (auth.me()?.cityName ?? '')" menuLabel="תפריט מנהל העיר" />`,
})
export class CityAdmin {
  protected readonly auth = inject(AuthService);
  protected readonly tabs: ShellTab[] = [
    { path: 'clusters', label: 'אשכולות', icon: 'clusters' },
    { path: 'communities', label: 'קהילות', icon: 'comms' },
    { path: 'streets', label: 'רחובות', icon: 'streets' },
    { path: 'users', label: 'משתמשים', icon: 'users' },
    { path: 'settings', label: 'הגדרות העיר', icon: 'city' },
  ];
}
