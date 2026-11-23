import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { NgTemplateOutlet } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { Api } from '../core/api';
import { errorMessage } from '../core/auth.service';
import { Dialog } from '../shared/dialog';
import { Icon } from '../shared/icon';
import { formatPhone } from '../operator/intake.model';

interface Leg { km: number | null; minutes: number | null }
interface Stop {
  kind: 'Pickup' | 'DropOff';
  distance: Leg;
  address: string | null;
  entrance?: string | null;
  driverNotes?: string | null;
  names?: string[];
  needsWheelchair?: boolean;
  needsWalker?: boolean;
  needsHelp?: boolean;
  phone?: string;
  comesBack?: boolean;
  plate?: string;
  name?: string | null;
  dropoffNote?: string | null;
  location: { lat: number; lon: number } | null;
}
interface Me {
  fullName: string;
  seats: number;
  status: 'Free' | 'Soon' | 'OnTrip' | 'Break' | 'Finished';
  breakAfterTrip: boolean;
  minutesToFree: number | null;
  hqPhone: string | null;
  cityName: string;
  trip: { id: string; status: 'Offered' | 'Active'; offeredAt: string; stops: Stop[]; passengers: number; totalKm: number | null; totalMinutes: number | null } | null;
}

const DECLINE_REASONS = ['רחוק מדי', 'אין מספיק מקום ברכב', 'צריך הפסקה', 'אחר'];
const OFFER_SECONDS = 120;

/**
 * Driver app (spec 5.4, 5.5, 5.7, prototype docs/driver.html). One main action per state, large buttons.
 * The location is sent with each button press; no background tracking. Checks for news every 10 seconds.
 */
@Component({
  selector: 'app-driver',
  imports: [FormsModule, Dialog, Icon, NgTemplateOutlet],
  templateUrl: './driver-app.html',
})
export class DriverAppPage implements OnInit, OnDestroy {
  private readonly api = inject(Api);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  protected readonly formatPhone = formatPhone;
  protected readonly reasons = DECLINE_REASONS;

  protected readonly me = signal<Me | null>(null);
  protected readonly error = signal('');
  protected readonly signedOut = signal(false);
  protected readonly busy = signal(false);
  protected readonly menuOpen = signal(false);
  protected readonly declineOpen = signal(false);
  protected readonly secondsLeft = signal(OFFER_SECONDS);
  protected declineReason = '';
  protected declineOther = '';
  protected readonly declineErr = signal('');
  private poll?: ReturnType<typeof setInterval>;
  private tick?: ReturnType<typeof setInterval>;
  private lastOfferId: string | null = null;

  protected readonly offer = computed(() => (this.me()?.trip?.status === 'Offered' ? this.me()!.trip : null));
  protected readonly trip = computed(() => (this.me()?.trip?.status === 'Active' ? this.me()!.trip : null));

  async ngOnInit() {
    const token = this.route.snapshot.paramMap.get('token');
    if (token) {
      try {
        await this.api.post('/api/driver/session', { token });
      } catch (e) {
        if (e instanceof HttpErrorResponse && e.status === 410) {
          // Already used on this phone? Then the session works; otherwise the link expired.
          if (!(await this.load())) this.error.set(errorMessage(e));
        }
      }
      await this.router.navigateByUrl('/driver', { replaceUrl: true });
    }
    await this.load();
    this.poll = setInterval(() => this.load(), 10_000);
    this.tick = setInterval(() => this.updateTimer(), 1000);
  }

  ngOnDestroy() {
    clearInterval(this.poll);
    clearInterval(this.tick);
  }

  private async load(): Promise<boolean> {
    try {
      const me = await this.api.get<Me>('/api/driver/me');
      const offerId = me.trip?.status === 'Offered' ? me.trip.id : null;
      if (offerId && offerId !== this.lastOfferId) this.ring();
      this.lastOfferId = offerId;
      this.me.set(me);
      this.signedOut.set(false);
      this.updateTimer();
      return true;
    } catch (e) {
      if (e instanceof HttpErrorResponse && e.status === 401) this.signedOut.set(true);
      return false;
    }
  }

  private updateTimer() {
    const o = this.offer();
    if (!o) return;
    const passed = (Date.now() - new Date(o.offeredAt).getTime()) / 1000;
    this.secondsLeft.set(Math.max(0, Math.round(OFFER_SECONDS - passed)));
  }

  protected timerText() {
    const s = this.secondsLeft();
    return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
  }

  /** A short tone when a new ride arrives (browsers allow sound after the driver touched the screen once). */
  private ring() {
    try {
      const ctx = new AudioContext();
      [0, 0.35].forEach((t) => {
        const o = ctx.createOscillator();
        const g = ctx.createGain();
        o.frequency.value = 880;
        g.gain.value = 0.2;
        o.connect(g).connect(ctx.destination);
        o.start(ctx.currentTime + t);
        o.stop(ctx.currentTime + t + 0.2);
      });
      navigator.vibrate?.([300, 150, 300]);
    } catch {
      /* no sound available */
    }
  }

  /** Every button press sends the current location, when the phone allows it. */
  private position(): Promise<{ lat?: number; lon?: number }> {
    return new Promise((resolve) => {
      if (!navigator.geolocation) return resolve({});
      navigator.geolocation.getCurrentPosition(
        (p) => resolve({ lat: p.coords.latitude, lon: p.coords.longitude }),
        () => resolve({}),
        { enableHighAccuracy: true, timeout: 4000, maximumAge: 30_000 },
      );
    });
  }

  protected async act(action: string, extra: Record<string, unknown> = {}) {
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set('');
    try {
      const pos = await this.position();
      await this.api.post('/api/driver/action', { action, ...pos, ...extra });
      this.menuOpen.set(false);
      await this.load();
      return true;
    } catch (e) {
      this.error.set(errorMessage(e));
      await this.load();
      return false;
    } finally {
      this.busy.set(false);
    }
  }

  protected openDecline() {
    this.declineReason = this.declineOther = '';
    this.declineErr.set('');
    this.declineOpen.set(true);
  }

  protected async sendDecline() {
    if (!this.declineReason) return this.declineErr.set('צריך לבחור סיבה');
    if (this.declineReason === 'אחר' && !this.declineOther.trim()) return this.declineErr.set('צריך לכתוב בקצרה למה');
    if (await this.act('Decline', { reason: this.declineReason, other: this.declineOther })) this.declineOpen.set(false);
  }

  protected waze(s: Stop) {
    return s.location
      ? `https://waze.com/ul?ll=${s.location.lat},${s.location.lon}&navigate=yes`
      : `https://waze.com/ul?q=${encodeURIComponent((s.address ?? '') + ' ' + (this.me()?.cityName ?? ''))}&navigate=yes`;
  }

  protected distText(s: Stop, i: number) {
    if (s.distance.km == null) return '';
    const from = i === 0 ? 'ממך' : s.kind === 'DropOff' ? 'מהאיסוף האחרון' : 'מהאיסוף הקודם';
    return `${s.distance.km} ק"מ · כ-${s.distance.minutes} דקות ${from}`;
  }

  protected pickupNo(i: number) {
    return this.me()!.trip!.stops.slice(0, i + 1).filter((s) => s.kind === 'Pickup').length;
  }
}
