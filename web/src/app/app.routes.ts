import { inject } from '@angular/core';
import { Router, Routes } from '@angular/router';
import { authGuard } from './core/auth.guard';
import { AuthService, Role } from './core/auth.service';

/** Only users holding the given role may open the area; others go to their own start page. */
const roleGuard = (role: Role) => () => {
  const me = inject(AuthService).me();
  return me?.roles.includes(role) ? true : inject(Router).parseUrl('/');
};

/** Sends each role to its own screen after sign-in. */
const startPage = () => {
  const me = inject(AuthService).me();
  const router = inject(Router);
  if (me?.role === 'CityAdmin') return router.parseUrl('/city/dashboard');
  if (me?.role === 'SystemAdmin') return router.parseUrl('/admin/live');
  if (me?.role === 'Operator') return router.parseUrl('/operator');
  if (me?.role === 'Dispatcher') return router.parseUrl('/dispatcher');
  return true;
};

export const routes: Routes = [
  { path: 'login', loadComponent: () => import('./pages/login').then((m) => m.LoginPage), title: 'כניסה · מטה רכב' },
  {
    path: 'set-password',
    loadComponent: () => import('./pages/set-password').then((m) => m.SetPasswordPage),
    title: 'קביעת סיסמה · מטה רכב',
  },
  {
    path: 'city',
    canActivate: [authGuard, roleGuard('CityAdmin')],
    loadComponent: () => import('./city/city-admin').then((m) => m.CityAdmin),
    children: [
      { path: 'dashboard', loadComponent: () => import('./city/dashboard').then((m) => m.DashboardPage), title: 'דשבורד · מטה רכב' },
      { path: 'clusters', loadComponent: () => import('./city/clusters').then((m) => m.ClustersPage), title: 'אשכולות · מטה רכב' },
      { path: 'communities', loadComponent: () => import('./city/list-manager').then((m) => m.CommunitiesPage), title: 'קהילות · מטה רכב' },
      { path: 'streets', loadComponent: () => import('./city/list-manager').then((m) => m.StreetsPage), title: 'רחובות · מטה רכב' },
      { path: 'base', loadComponent: () => import('./city/voter-base').then((m) => m.VoterBasePage), title: 'בייס מצביעים · מטה רכב' },
      { path: 'users', loadComponent: () => import('./city/users').then((m) => m.UsersPage), title: 'משתמשים · מטה רכב' },
      { path: 'settings', loadComponent: () => import('./city/settings').then((m) => m.SettingsPage), title: 'הגדרות העיר · מטה רכב' },
      { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
    ],
  },
  {
    path: 'operator',
    canActivate: [authGuard, roleGuard('Operator')],
    loadComponent: () => import('./operator/operator').then((m) => m.OperatorPage),
    title: 'מוקדן · מטה רכב',
  },
  // Driver app: its own sign-in by personal link; no staff sign-in.
  { path: 'driver/:token', loadComponent: () => import('./driver/driver-app').then((m) => m.DriverAppPage), title: 'נהג · מטה רכב' },
  { path: 'driver', loadComponent: () => import('./driver/driver-app').then((m) => m.DriverAppPage), title: 'נהג · מטה רכב' },
  {
    path: 'dispatcher',
    canActivate: [authGuard, roleGuard('Dispatcher')],
    loadComponent: () => import('./dispatcher/dispatcher').then((m) => m.DispatcherPage),
    title: 'משבץ · מטה רכב',
  },
  {
    path: 'admin',
    canActivate: [authGuard, roleGuard('SystemAdmin')],
    loadComponent: () => import('./admin/system-admin').then((m) => m.SystemAdmin),
    children: [
      { path: 'cities', loadComponent: () => import('./admin/system-admin').then((m) => m.CitiesPage), title: 'ערים · מטה רכב' },
      { path: 'operations', loadComponent: () => import('./admin/system-admin').then((m) => m.OperationsPage), title: 'מבצעי בחירות · מטה רכב' },
      { path: 'live', loadComponent: () => import('./admin/system-pages').then((m) => m.LivePage), title: 'תמונת מצב · מטה רכב' },
      { path: 'catalog', loadComponent: () => import('./admin/system-pages').then((m) => m.CatalogPage), title: 'קהילות · מטה רכב' },
      { path: 'reports', loadComponent: () => import('./admin/system-pages').then((m) => m.ReportsPage), title: 'דוחות · מטה רכב' },
      { path: 'certificates', loadComponent: () => import('./admin/system-pages').then((m) => m.CertificatesPage), title: 'אישורי מחיקה · מטה רכב' },
      { path: 'audit', loadComponent: () => import('./admin/system-pages').then((m) => m.AuditPage), title: 'יומן פעולות · מטה רכב' },
      { path: 'sysadmins', loadComponent: () => import('./admin/system-pages').then((m) => m.SysAdminsPage), title: 'מנהלי מערכת · מטה רכב' },
      { path: 'security', loadComponent: () => import('./admin/system-pages').then((m) => m.SecurityPage), title: 'מדיניות אבטחה · מטה רכב' },
      { path: 'party', loadComponent: () => import('./admin/system-pages').then((m) => m.PartyPage), title: 'פרטיות והגדרות · מטה רכב' },
      { path: '', pathMatch: 'full', redirectTo: 'live' },
    ],
  },
  {
    path: '',
    pathMatch: 'full',
    canActivate: [authGuard, startPage],
    loadComponent: () => import('./pages/home').then((m) => m.HomePage),
    title: 'מטה רכב',
  },
  { path: '**', redirectTo: '' },
];
