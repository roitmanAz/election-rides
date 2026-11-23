import { Injectable, inject, signal } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

export type Role = 'Operator' | 'Dispatcher' | 'CityAdmin' | 'SystemAdmin';

export interface Me {
  id: string;
  fullName: string;
  email: string;
  role: Role;
  cityId: string | null;
  cityName: string | null;
}

export type LoginResult =
  | { status: 'signed-in'; user: Me }
  | { status: 'code-sent'; challengeId: string; email: string };

export const ROLE_NAMES: Record<Role, string> = {
  Operator: 'מוקדן',
  Dispatcher: 'משבץ',
  CityAdmin: 'מנהל עיר',
  SystemAdmin: 'מנהל מערכת',
};

/** Turns a failed request into the Hebrew message the server sent, or a general one. */
export function errorMessage(e: unknown): string {
  if (e instanceof HttpErrorResponse) {
    if (e.status === 0) return 'אין חיבור לשרת. כדאי לבדוק את החיבור לאינטרנט ולנסות שוב.';
    if (e.status === 429) return 'יותר מדי ניסיונות. אפשר לנסות שוב בעוד דקה.';
    if (typeof e.error?.error === 'string') return e.error.error;
  }
  return 'משהו השתבש. אפשר לנסות שוב.';
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  readonly me = signal<Me | null>(null);

  async loadMe(): Promise<Me | null> {
    try {
      const me = await firstValueFrom(this.http.get<Me>('/api/auth/me'));
      this.me.set(me);
      return me;
    } catch {
      this.me.set(null);
      return null;
    }
  }

  async login(email: string, password: string): Promise<LoginResult> {
    const r = await firstValueFrom(this.http.post<LoginResult>('/api/auth/login', { email, password }));
    if (r.status === 'signed-in') this.me.set(r.user);
    return r;
  }

  async verify(challengeId: string, code: string): Promise<Me> {
    const r = await firstValueFrom(
      this.http.post<{ status: 'signed-in'; user: Me }>('/api/auth/verify', { challengeId, code }),
    );
    this.me.set(r.user);
    return r.user;
  }

  resend(challengeId: string) {
    return firstValueFrom(
      this.http.post<{ challengeId: string; email: string }>('/api/auth/resend', { challengeId }),
    );
  }

  setPassword(token: string, password: string) {
    return firstValueFrom(this.http.post<{ email: string }>('/api/auth/set-password', { token, password }));
  }

  async logout(): Promise<void> {
    try {
      await firstValueFrom(this.http.post('/api/auth/logout', null));
    } finally {
      this.me.set(null);
    }
  }
}
