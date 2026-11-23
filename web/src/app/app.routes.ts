import { inject } from '@angular/core';
import { Router, Routes } from '@angular/router';
import { authGuard } from './core/auth.guard';
import { AuthService, Role } from './core/auth.service';

/** Only the given role may open the area; others go to their own start page. */
const roleGuard = (role: Role) => () => {
  const me = inject(AuthService).me();
  return me?.role === role ? true : inject(Router).parseUrl('/');
};

/** Sends each role to its own screen after sign-in. */
const startPage = () => {
  const me = inject(AuthService).me();
  const router = inject(Router);
  if (me?.role === 'CityAdmin') return router.parseUrl('/city/clusters');
  if (me?.role === 'SystemAdmin') return router.parseUrl('/admin/cities');
  if (me?.role === 'Operator') return router.parseUrl('/operator');
  if (me?.role === 'Dispatcher') return router.parseUrl('/dispatcher');
  return true;
};

export const routes: Routes = [
  { path: 'login', loadComponent: () => import('./pages/login').then((m) => m.LoginPage), title: 'כניסה · מטה הסעות' },
  {
    path: 'set-password',
    loadComponent: () => import('./pages/set-password').then((m) => m.SetPasswordPage),
    title: 'קביעת סיסמה · מטה הסעות',
  },
  {
    path: 'city',
    canActivate: [authGuard, roleGuard('CityAdmin')],
    loadComponent: () => import('./city/city-admin').then((m) => m.CityAdmin),
    children: [
      { path: 'clusters', loadComponent: () => import('./city/clusters').then((m) => m.ClustersPage), title: 'אשכולות · מטה הסעות' },
      { path: 'communities', loadComponent: () => import('./city/list-manager').then((m) => m.CommunitiesPage), title: 'קהילות · מטה הסעות' },
      { path: 'streets', loadComponent: () => import('./city/list-manager').then((m) => m.StreetsPage), title: 'רחובות · מטה הסעות' },
      { path: 'users', loadComponent: () => import('./city/users').then((m) => m.UsersPage), title: 'משתמשים · מטה הסעות' },
      { path: 'settings', loadComponent: () => import('./city/settings').then((m) => m.SettingsPage), title: 'הגדרות העיר · מטה הסעות' },
      { path: '', pathMatch: 'full', redirectTo: 'clusters' },
    ],
  },
  {
    path: 'operator',
    canActivate: [authGuard, roleGuard('Operator')],
    loadComponent: () => import('./operator/operator').then((m) => m.OperatorPage),
    title: 'מוקדן · מטה הסעות',
  },
  {
    path: 'dispatcher',
    canActivate: [authGuard, roleGuard('Dispatcher')],
    loadComponent: () => import('./dispatcher/dispatcher').then((m) => m.DispatcherPage),
    title: 'משבץ · מטה הסעות',
  },
  {
    path: 'admin',
    canActivate: [authGuard, roleGuard('SystemAdmin')],
    loadComponent: () => import('./admin/system-admin').then((m) => m.SystemAdmin),
    children: [
      { path: 'cities', loadComponent: () => import('./admin/system-admin').then((m) => m.CitiesPage), title: 'ערים · מטה הסעות' },
      { path: 'operations', loadComponent: () => import('./admin/system-admin').then((m) => m.OperationsPage), title: 'מבצעי בחירות · מטה הסעות' },
      { path: '', pathMatch: 'full', redirectTo: 'cities' },
    ],
  },
  {
    path: '',
    pathMatch: 'full',
    canActivate: [authGuard, startPage],
    loadComponent: () => import('./pages/home').then((m) => m.HomePage),
    title: 'מטה הסעות',
  },
  { path: '**', redirectTo: '' },
];
