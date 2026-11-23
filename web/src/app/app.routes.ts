import { Routes } from '@angular/router';
import { authGuard } from './core/auth.guard';

export const routes: Routes = [
  { path: 'login', loadComponent: () => import('./pages/login').then((m) => m.LoginPage), title: 'כניסה · מטה הסעות' },
  {
    path: 'set-password',
    loadComponent: () => import('./pages/set-password').then((m) => m.SetPasswordPage),
    title: 'קביעת סיסמה · מטה הסעות',
  },
  {
    path: '',
    canActivate: [authGuard],
    loadComponent: () => import('./pages/home').then((m) => m.HomePage),
    title: 'מטה הסעות',
  },
  { path: '**', redirectTo: '' },
];
