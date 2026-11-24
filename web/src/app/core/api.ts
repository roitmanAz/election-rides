import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpHeaders } from '@angular/common/http';

/** Marks the screen's automatic refreshes: they do not count as the user's activity (spec 3.2). */
const BACKGROUND = { headers: new HttpHeaders({ 'X-Background': '1' }) };
import { firstValueFrom } from 'rxjs';

/** Thin promise wrapper over HttpClient for screen code. */
@Injectable({ providedIn: 'root' })
export class Api {
  private readonly http = inject(HttpClient);
  get<T>(url: string, background = false) {
    return firstValueFrom(this.http.get<T>(url, background ? BACKGROUND : {}));
  }
  post<T>(url: string, body: unknown = null) {
    return firstValueFrom(this.http.post<T>(url, body));
  }
  put<T>(url: string, body: unknown) {
    return firstValueFrom(this.http.put<T>(url, body));
  }
  delete(url: string) {
    return firstValueFrom(this.http.delete(url));
  }
}
