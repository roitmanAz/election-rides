import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

/** Thin promise wrapper over HttpClient for screen code. */
@Injectable({ providedIn: 'root' })
export class Api {
  private readonly http = inject(HttpClient);
  get<T>(url: string) {
    return firstValueFrom(this.http.get<T>(url));
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
