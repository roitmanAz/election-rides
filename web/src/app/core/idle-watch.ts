import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Api } from './api';
import { AuthService } from './auth.service';
import { Dialog } from '../shared/dialog';

const WARN_MS = 2 * 60_000;
/** The server notes activity at most every 20 seconds; ending a little early keeps the two in step. */
const MARGIN_MS = 45_000;
const PING_MS = 20_000;
const KEY = 'er-last-active';

/**
 * Warns 2 minutes before the session ends for lack of activity (spec 3.2, security policy), with a countdown
 * and "I'm here". Only the user's own activity counts (mouse, keys, touch), in any tab of the system; the
 * screens' automatic refreshes do not. At zero the user is signed out and sent to the sign-in page.
 */
@Component({
  selector: 'app-idle-watch',
  imports: [Dialog],
  host: {
    '(document:pointerdown)': 'activity()',
    '(document:keydown)': 'activity()',
    '(document:wheel)': 'activity()',
    '(document:touchstart)': 'activity()',
    '(document:mousemove)': 'activity()',
    '(window:storage)': 'fromOtherTab($event)',
  },
  template: `
    <app-dialog [open]="warning()" title="עוד מעט תנותק מהמערכת" [width]="440" (closed)="stay()">
      <p>לא הייתה פעילות כבר זמן מה. כדי לשמור על הנתונים, המערכת תנתק אותך בעוד:</p>
      <p class="my-4 text-center text-4xl font-semibold tabular-nums" [class.text-bad]="secondsLeft() <= 30" aria-live="polite">{{ clock() }}</p>
      <div class="flex gap-2">
        <button type="button" class="btn-primary flex-1" (click)="stay()">אני כאן, להמשיך</button>
        <button type="button" class="btn-secondary" (click)="leave()">יציאה</button>
      </div>
    </app-dialog>
  `,
})
export class IdleWatch implements OnInit, OnDestroy {
  private readonly auth = inject(AuthService);
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  private lastActive = Date.now();
  private lastPing = Date.now();
  private lastStored = 0;
  private timer?: ReturnType<typeof setInterval>;
  protected readonly secondsLeft = signal(Infinity);
  protected readonly warning = computed(() => this.watching() && this.secondsLeft() <= WARN_MS / 1000);
  protected readonly clock = computed(() => {
    const s = Math.max(0, this.secondsLeft());
    return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
  });

  /** Staff only: drivers sign in with their personal link and have no idle limit. */
  private watching() {
    const me = this.auth.me();
    return !!me && !location.pathname.startsWith('/driver');
  }

  ngOnInit() {
    this.timer = setInterval(() => this.tick(), 1000);
  }

  ngOnDestroy() {
    clearInterval(this.timer);
  }

  /** Real activity; while the warning shows, only the button counts. */
  protected activity(force = false) {
    if (!this.watching() || (this.warning() && !force)) return;
    const now = Date.now();
    this.lastActive = now;
    if (now - this.lastStored > 5000) {
      this.lastStored = now;
      try {
        localStorage.setItem(KEY, String(now));
      } catch {
        // Private mode: this tab still counts its own activity.
      }
    }
    // Tell the server now and then, so the session stays open while the user works without saving anything.
    if (now - this.lastPing > PING_MS) {
      this.lastPing = now;
      this.api.get('/api/auth/me').catch(() => {});
    }
  }

  protected fromOtherTab(e: StorageEvent) {
    if (e.key === KEY && e.newValue) this.lastActive = Math.max(this.lastActive, +e.newValue);
  }

  protected stay() {
    this.lastPing = 0;
    this.activity(true);
    this.secondsLeft.set(Infinity);
  }

  protected async leave() {
    this.secondsLeft.set(Infinity);
    await this.auth.logout();
    await this.router.navigateByUrl('/login');
  }

  private async tick() {
    const me = this.auth.me();
    if (!this.watching() || !me) {
      this.secondsLeft.set(Infinity);
      return;
    }
    const left = this.lastActive + me.idleMinutes * 60_000 - MARGIN_MS - Date.now();
    this.secondsLeft.set(Math.ceil(left / 1000));
    if (left <= 0) {
      this.secondsLeft.set(Infinity);
      try {
        await this.auth.logout();
      } finally {
        await this.router.navigate(['/login'], { queryParams: { expired: 1 } });
      }
    }
  }
}
