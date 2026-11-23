import { Component, OnInit, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';

type Health = 'checking' | 'ok' | 'degraded' | 'down';

@Component({
  selector: 'app-root',
  templateUrl: './app.html',
})
export class App implements OnInit {
  private readonly http = inject(HttpClient);
  protected readonly health = signal<Health>('checking');

  ngOnInit(): void {
    this.http.get<{ status: string }>('/api/health').subscribe({
      next: (r) => this.health.set(r.status === 'ok' ? 'ok' : 'degraded'),
      error: (e) => this.health.set(e?.status === 503 ? 'degraded' : 'down'),
    });
  }
}
