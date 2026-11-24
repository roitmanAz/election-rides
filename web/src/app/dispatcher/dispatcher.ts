import { Component, OnDestroy, OnInit, computed, inject, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { Api } from '../core/api';
import { AuthService, errorMessage } from '../core/auth.service';
import { Dialog } from '../shared/dialog';
import { Icon } from '../shared/icon';
import { RoleSwitch } from '../shared/role-switch';
import { Select, textOptions } from '../shared/select';
import { CANCEL_REASONS, formatPhone, phoneDigits, phoneError } from '../operator/intake.model';
import { CityMap } from './city-map';
import {
  ClusterRow, DRIVER_STATUS, DriverRow, KIND_NAMES, LatLon, QueueRow, kindOf, script, waitClass, whyNot,
} from './dispatch.model';

interface Proposal {
  driverId: string;
  requestIds: string[];
  /** The dispatcher changed this proposal; it is kept when the automatic proposals refresh. */
  manual: boolean;
}

type Filter = 'all' | 'Men' | 'Women' | 'Family' | 'wc' | 'pending';

/**
 * Dispatcher screen (spec 5.3, prototype docs/dispatcher.html): the queue, request details and the map,
 * a proposal tab per free or soon-free driver, and the drivers list with statuses. Live: reloads on every change.
 */
@Component({
  selector: 'app-dispatcher',
  imports: [FormsModule, Dialog, Icon, CityMap, RoleSwitch, Select],
  templateUrl: './dispatcher.html',
})
export class DispatcherPage implements OnInit, OnDestroy {
  private readonly api = inject(Api);
  protected readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  protected readonly kindOf = kindOf;
  protected readonly waitClass = waitClass;
  protected readonly kindNames = KIND_NAMES;
  protected readonly driverStatus = DRIVER_STATUS;
  protected readonly formatPhone = formatPhone;
  protected readonly cancelReasons = CANCEL_REASONS;
  protected readonly cancelOpts = textOptions(CANCEL_REASONS);
  protected readonly seatOpts = textOptions(['1', '2', '3', '4', '5', '6', '7', '8']);
  protected readonly filters: { key: Filter; label: string }[] = [
    { key: 'all', label: 'הכול' },
    { key: 'Men', label: 'גברים' },
    { key: 'Women', label: 'נשים' },
    { key: 'Family', label: 'משפחות' },
    { key: 'wc', label: 'נגישות' },
    { key: 'pending', label: 'ממתין לאימות' },
  ];

  protected readonly active = signal(true);
  protected readonly queue = signal<QueueRow[]>([]);
  protected readonly drivers = signal<DriverRow[]>([]);
  protected readonly clusters = signal<ClusterRow[]>([]);
  protected readonly proposals = signal<Proposal[]>([]);
  protected readonly filter = signal<Filter>('all');
  protected readonly selectedId = signal<string | null>(null);
  protected readonly propIdx = signal(0);
  protected readonly openDriverId = signal<string | null>(null);
  protected readonly notice = signal('');
  protected readonly toast = signal('');
  protected readonly mapBig = signal(false);
  private readonly map = viewChild<CityMap>('map');

  /** Queue order (spec 6): "leave by" within 30 minutes first, nearest first; then the longest wait. */
  protected readonly ordered = computed(() =>
    [...this.queue()].sort((a, b) =>
      a.deadlineSoon !== b.deadlineSoon ? (a.deadlineSoon ? -1 : 1)
        : a.deadlineSoon ? (a.minutesToDeadline ?? 0) - (b.minutesToDeadline ?? 0)
        : b.waitMinutes - a.waitMinutes,
    ),
  );
  protected readonly shown = computed(() => {
    const f = this.filter();
    return this.ordered().filter((r) =>
      f === 'all' ? true : f === 'wc' ? r.needsWheelchair : f === 'pending' ? r.pending : r.kind === f,
    );
  });
  protected readonly selected = computed(() => this.queue().find((r) => r.id === this.selectedId()) ?? null);
  protected readonly available = computed(() =>
    this.drivers().filter((d) => (d.status === 'Free' || d.status === 'Soon') && !d.breakAfterTrip)
      .sort((a, b) => (a.status === b.status ? (a.minutesToFree ?? 0) - (b.minutesToFree ?? 0) : a.status === 'Free' ? -1 : 1)),
  );
  protected readonly currentProp = computed(() => this.proposals()[this.propIdx()] ?? null);
  protected readonly propDriver = computed(() => this.drivers().find((d) => d.id === this.currentProp()?.driverId) ?? null);
  protected readonly propStops = computed(() =>
    (this.currentProp()?.requestIds ?? []).map((id) => this.queue().find((r) => r.id === id)).filter(Boolean) as QueueRow[],
  );
  protected readonly route = computed<LatLon[]>(() => {
    const pts = this.propStops().map((r) => r.location).filter(Boolean) as LatLon[];
    const dests = [...new Set(this.propStops().flatMap((r) => r.clusterIds))]
      .map((id) => this.clusters().find((c) => c.id === id)?.location).filter(Boolean) as LatLon[];
    const start = this.propDriver()?.location;
    return [...(start ? [start] : []), ...pts, ...dests];
  });

  private hub?: HubConnection;
  private reloadTimer?: ReturnType<typeof setTimeout>;
  private clock?: ReturnType<typeof setInterval>;

  async ngOnInit() {
    this.clusters.set(await this.api.get<ClusterRow[]>('/api/city/clusters'));
    await this.reload();
    this.hub = new HubConnectionBuilder().withUrl('/hubs/city').withAutomaticReconnect().configureLogging(LogLevel.Warning).build();
    this.hub.on('changed', () => this.scheduleReload());
    this.hub.onreconnected(() => this.scheduleReload());
    try {
      await this.hub.start();
    } catch {
      this.notice.set('אין עדכון חי כרגע. המסך מתעדכן כל דקה.');
    }
    // Wait minutes and the 2-minute offer alert change by themselves; refresh every 30 seconds even without changes.
    this.clock = setInterval(() => this.reload(), 30_000);
  }

  ngOnDestroy() {
    this.hub?.stop();
    clearInterval(this.clock);
    clearTimeout(this.reloadTimer);
  }

  private scheduleReload() {
    clearTimeout(this.reloadTimer);
    this.reloadTimer = setTimeout(() => this.reload(), 250);
  }

  protected async reload() {
    const s = await this.api.get<{ active: boolean; queue?: QueueRow[]; drivers?: DriverRow[] }>('/api/dispatch/state');
    this.active.set(s.active);
    if (!s.active) return;
    this.queue.set(s.queue ?? []);
    this.drivers.set(s.drivers ?? []);
    if (this.selectedId() && !this.queue().some((r) => r.id === this.selectedId())) this.selectedId.set(null);
    await this.reloadProposals();
  }

  /** Automatic proposals, keeping the ones the dispatcher changed (as long as the driver and requests are still there). */
  private async reloadProposals() {
    const auto = await this.api.get<{ driverId: string; requestIds: string[] }[]>('/api/dispatch/proposals');
    const queued = new Set(this.queue().map((r) => r.id));
    const avail = new Set(this.available().map((d) => d.id));
    const manual = this.proposals().filter((p) => p.manual && avail.has(p.driverId))
      .map((p) => ({ ...p, requestIds: p.requestIds.filter((id) => queued.has(id)) }));
    const takenByManual = new Set(manual.flatMap((p) => p.requestIds));
    const curDriver = this.currentProp()?.driverId;
    const list = this.available().map((d) =>
      manual.find((m) => m.driverId === d.id) ?? {
        driverId: d.id,
        requestIds: (auto.find((a) => a.driverId === d.id)?.requestIds ?? []).filter((id) => !takenByManual.has(id)),
        manual: false,
      },
    );
    this.proposals.set(list);
    const idx = list.findIndex((p) => p.driverId === curDriver);
    this.propIdx.set(idx >= 0 ? idx : 0);
  }

  // ---------- Queue and details ----------
  protected cluster(id: string | null | undefined) {
    return this.clusters().find((c) => c.id === id);
  }

  protected plate(id: string) {
    const c = this.cluster(id);
    return c ? (c.number != null ? String(c.number) : c.shortName) : '';
  }

  protected inProposal(r: QueueRow) {
    return this.proposals().some((p) => p.requestIds.includes(r.id));
  }

  protected whenText(r: QueueRow) {
    if (r.timeMode === 'Now') return 'עכשיו';
    if (r.timeMode === 'Between') return `${r.windowFrom?.slice(0, 5)}–${r.windowTo?.slice(0, 5)}`;
    return `עד ${r.windowTo?.slice(0, 5)}`;
  }

  /** Why the selected request cannot join the current proposal (short, spec 5.3); empty when it can. */
  protected joinReason(): string {
    const r = this.selected();
    const d = this.propDriver();
    if (!r || !d) return '';
    return whyNot(d, this.propStops(), r);
  }

  protected addToProposal() {
    const r = this.selected();
    if (!r || this.joinReason()) return;
    // A request can be in one proposal only.
    this.proposals.update((list) => list.map((p, i) =>
      i === this.propIdx() ? { ...p, requestIds: [...p.requestIds, r.id], manual: true }
        : p.requestIds.includes(r.id) ? { ...p, requestIds: p.requestIds.filter((x) => x !== r.id), manual: true } : p,
    ));
  }

  protected removeStop(id: string) {
    this.proposals.update((list) => list.map((p, i) => (i === this.propIdx() ? { ...p, requestIds: p.requestIds.filter((x) => x !== id), manual: true } : p)));
  }

  protected scriptLines() {
    return script(this.propStops(), (id) => this.cluster(id)?.name ?? '');
  }

  protected async confirm() {
    const p = this.currentProp();
    const d = this.propDriver();
    if (!p || !d || !p.requestIds.length) return;
    try {
      await this.api.post('/api/dispatch/trips', { driverId: p.driverId, requestIds: p.requestIds });
      this.proposals.update((list) => list.filter((x) => x.driverId !== p.driverId));
      this.showToast(`הנסיעה נשלחה ל${d.fullName}`);
      await this.reload();
    } catch (e) {
      this.notice.set(errorMessage(e));
      await this.reload();
    }
  }

  protected async verify(r: QueueRow) {
    await this.api.post(`/api/dispatch/requests/${r.id}/verify`);
    await this.reload();
  }

  // Cancel (reason required; "other" needs text)
  protected readonly cancelOpen = signal(false);
  protected cancelReason = '';
  protected cancelOther = '';
  protected readonly cancelErr = signal('');

  protected openCancel() {
    this.cancelReason = this.cancelOther = '';
    this.cancelErr.set('');
    this.cancelOpen.set(true);
  }

  protected async confirmCancel() {
    const r = this.selected();
    if (!r) return;
    if (!this.cancelReason) return this.cancelErr.set('צריך לבחור סיבה');
    if (this.cancelReason === 'אחר' && !this.cancelOther.trim()) return this.cancelErr.set('צריך לפרט בקצרה');
    try {
      await this.api.post(`/api/intake/requests/${r.id}/cancel`, { reason: this.cancelReason, other: this.cancelOther });
      this.cancelOpen.set(false);
      this.selectedId.set(null);
      this.showToast(`פנייה מס' ${r.number} בוטלה`);
      await this.reload();
    } catch (e) {
      this.cancelErr.set(errorMessage(e));
    }
  }

  // ---------- Drivers ----------
  protected driverLine(d: DriverRow) {
    if (d.trip?.status === 'Offered') return this.offerLate(d) ? 'לא אישר תוך 2 דקות' : 'ממתין לאישור';
    if (d.status === 'Soon') return `מתפנה בעוד ${d.minutesToFree ?? 0} דק'`;
    if (d.status === 'OnTrip') return d.breakAfterTrip ? 'בנסיעה · הפסקה אחריה' : 'בנסיעה';
    return DRIVER_STATUS[d.status];
  }

  /** An offer not accepted within 2 minutes alerts the dispatcher (spec 5.4). */
  protected offerLate(d: DriverRow) {
    return d.trip?.status === 'Offered' && Date.now() - new Date(d.trip.offeredAt).getTime() > 120_000;
  }

  protected async withdraw(d: DriverRow) {
    if (!d.trip) return;
    try {
      await this.api.post(`/api/dispatch/trips/${d.trip.id}/withdraw`);
      await this.reload();
    } catch (e) {
      this.notice.set(errorMessage(e));
    }
  }

  /** Personal link for the driver app; the dispatcher sends it by WhatsApp or SMS from their own phone. */
  protected readonly linkFor = signal<{ name: string; url: string } | null>(null);
  protected readonly copied = signal(false);
  protected async makeLink(d: DriverRow) {
    try {
      const r = await this.api.post<{ url: string }>(`/api/dispatch/drivers/${d.id}/link`);
      this.copied.set(false);
      this.linkFor.set({ name: d.fullName, url: r.url });
    } catch (e) {
      this.notice.set(errorMessage(e));
    }
  }
  protected async copyLink() {
    const l = this.linkFor();
    if (!l) return;
    try {
      await navigator.clipboard.writeText(l.url);
      this.copied.set(true);
    } catch {
      (document.getElementById('linkText') as HTMLInputElement | null)?.select();
    }
  }

  protected riders(d: DriverRow) {
    return (d.trip?.stops ?? []).filter((s) => s.kind === 'Pickup');
  }

  protected async action(d: DriverRow, action: string) {
    try {
      await this.api.post(`/api/dispatch/drivers/${d.id}/action`, { action });
      await this.reload();
    } catch (e) {
      this.notice.set(errorMessage(e));
    }
  }

  // New or edited driver: phone first; a known phone offers "is this him?" (spec 5.7)
  protected readonly drvOpen = signal(false);
  protected readonly drvEditId = signal<string | null>(null);
  protected readonly known = signal<{ id: string; fullName: string; seats: number; onList: boolean } | null>(null);
  protected readonly drvErr = signal<{ phone?: string; name?: string; seats?: string; app?: string; save?: string }>({});
  protected drv = { phone: '', fullName: '', seats: '' as string, wheelchairCar: false, hasSmartphone: null as boolean | null };

  protected openNewDriver() {
    this.drv = { phone: '', fullName: '', seats: '', wheelchairCar: false, hasSmartphone: null };
    this.drvEditId.set(null);
    this.known.set(null);
    this.drvErr.set({});
    this.drvOpen.set(true);
  }

  protected openEditDriver(d: DriverRow) {
    this.drv = { phone: formatPhone(d.phone), fullName: d.fullName, seats: String(d.seats), wheelchairCar: d.wheelchairCar, hasSmartphone: d.hasSmartphone };
    this.drvEditId.set(d.id);
    this.known.set(null);
    this.drvErr.set({});
    this.drvOpen.set(true);
  }

  protected async checkDriverPhone() {
    const err = phoneError(this.drv.phone);
    this.drvErr.update((e) => ({ ...e, phone: err }));
    if (err || this.drvEditId()) return;
    const r = await this.api.get<{ found: boolean; onList?: boolean; id?: string; fullName?: string; seats?: number }>(
      `/api/dispatch/drivers/lookup/${phoneDigits(this.drv.phone)}`,
    );
    this.known.set(r.found ? { id: r.id!, fullName: r.fullName!, seats: r.seats!, onList: !!r.onList } : null);
  }

  protected async rejoin() {
    const k = this.known();
    if (!k) return;
    try {
      await this.api.post(`/api/dispatch/drivers/${k.id}/rejoin`);
      this.drvOpen.set(false);
      this.showToast(`${k.fullName} חזר לרשימה כפנוי`);
      await this.reload();
    } catch (e) {
      this.drvErr.update((x) => ({ ...x, save: errorMessage(e) }));
    }
  }

  protected async saveDriver() {
    const e = {
      phone: phoneError(this.drv.phone),
      name: this.drv.fullName.trim() ? '' : 'צריך למלא שם',
      seats: /^[1-8]$/.test(this.drv.seats) ? '' : 'צריך לבחור מספר מקומות',
      app: this.drv.hasSmartphone === null ? 'צריך לבחור כן או לא' : '',
    };
    this.drvErr.set(e);
    if (Object.values(e).some(Boolean)) return;
    const body = { phone: phoneDigits(this.drv.phone), fullName: this.drv.fullName, seats: +this.drv.seats, wheelchairCar: this.drv.wheelchairCar, hasSmartphone: this.drv.hasSmartphone };
    try {
      const id = this.drvEditId();
      if (id) await this.api.put(`/api/dispatch/drivers/${id}`, body);
      else await this.api.post('/api/dispatch/drivers', body);
      this.drvOpen.set(false);
      await this.reload();
    } catch (err) {
      this.drvErr.update((x) => ({ ...x, save: errorMessage(err) }));
    }
  }

  // ---------- Misc ----------
  private showToast(text: string) {
    this.toast.set(text);
    setTimeout(() => this.toast() === text && this.toast.set(''), 4000);
  }

  protected fitMap() {
    this.map()?.fit();
  }

  protected async logout() {
    await this.auth.logout();
    await this.router.navigateByUrl('/login');
  }
}
