import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { Api } from '../core/api';
import { Icon } from '../shared/icon';

interface Dashboard {
  active: boolean;
  requestsToday: number;
  queued: number;
  averageWait: number;
  longestWait: number;
  arrived: number;
  cancelled: number;
  perHalfHour: { from: string; count: number }[];
  drivers: { active: number; free: number; soon: number; onTrip: number; onBreak: number };
  cancelReasons: { reason: string; count: number }[];
  operators: { name: string; count: number }[];
}

/**
 * City admin dashboard (spec 3.1). The chart runs left to right in time (as asked), with the exact number
 * on hover or focus, and a table view of the same numbers. Refreshes every minute.
 */
@Component({
  selector: 'app-dashboard',
  imports: [Icon],
  template: `
    @if (d(); as v) {
      @if (!v.active) {
        <p class="notice">העיר לא משתתפת עכשיו במבצע בחירות פעיל. הנתונים יופיעו כשמנהל המערכת יפתח מבצע.</p>
      } @else {
        <div class="grid gap-4">
          <div class="grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-6">
            @for (t of tiles(); track t.label) {
              <div class="card p-4" [class.shadow-[inset_0_0_0_2px_var(--color-bad),var(--shadow-card)]]="t.alert">
                <div class="text-sm font-medium text-muted">{{ t.label }}</div>
                <div class="mt-1 text-3xl font-semibold tabular-nums" [class.text-bad]="t.alert">{{ t.value }}@if (t.unit) { <small class="ms-1 text-sm font-medium text-muted">{{ t.unit }}</small> }</div>
                @if (t.alert) { <div class="mt-1 inline-flex items-center gap-1 text-sm font-medium text-bad"><app-icon name="warn" [size]="14" /> מעל 30 דקות</div> }
              </div>
            }
          </div>

          <section class="card p-5">
            <div class="mb-3 flex flex-wrap items-center gap-3">
              <h2 class="text-lg font-semibold">פניות לפי חצי שעה</h2>
              <button type="button" class="btn-secondary ms-auto h-9 px-3 text-sm" (click)="asTable.set(!asTable())">{{ asTable() ? 'הצג כתרשים' : 'הצג כטבלה' }}</button>
            </div>
            @if (!v.perHalfHour.length) {
              <p class="text-muted">עוד אין פניות היום.</p>
            } @else if (asTable()) {
              <div class="overflow-x-auto"><table class="w-full max-w-md text-sm"><thead><tr class="text-muted"><th class="py-1 text-start font-medium">שעה</th><th class="py-1 text-start font-medium">פניות</th></tr></thead>
                <tbody>@for (b of v.perHalfHour; track b.from) { <tr class="border-t border-line"><td class="py-1 tabular-nums">{{ b.from }}</td><td class="py-1 tabular-nums">{{ b.count }}</td></tr> }</tbody></table></div>
            } @else {
              <!-- Time runs left to right, so the chart is laid out left to right even on a right-to-left page. -->
              <div dir="ltr" class="grid grid-cols-[auto_minmax(0,1fr)] gap-x-2">
                <!-- Scale: a line and a number every step (5, or more when there are many requests). -->
                <div class="relative h-48 w-7 text-[11px] tabular-nums text-muted">
                  @for (t of ticks(); track t) {
                    <span class="absolute right-0 translate-y-1/2 leading-none" [style.bottom.%]="(t / scaleMax()) * 100">{{ t }}</span>
                  }
                </div>
                <div class="relative flex h-48 items-end gap-1 border-b border-line">
                  @for (t of ticks(); track t) {
                    @if (t > 0) { <span class="pointer-events-none absolute inset-x-0 border-t border-line" [style.bottom.%]="(t / scaleMax()) * 100"></span> }
                  }
                  @for (b of v.perHalfHour; track b.from; let i = $index) {
                    <button type="button" class="relative flex h-full min-w-0 flex-1 items-end focus:outline-none"
                      [attr.aria-label]="b.from + ': ' + b.count + ' פניות'" (mouseenter)="hover.set(i)" (mouseleave)="hover.set(null)" (focus)="hover.set(i)" (blur)="hover.set(null)">
                      <span class="relative mx-auto block w-full max-w-12 rounded-t-[4px] bg-action transition-opacity" [style.height.%]="(b.count / scaleMax()) * 100"
                        [class.opacity-70]="hover() !== null && hover() !== i">
                        @if (hover() === i) {
                          <span class="absolute bottom-full left-1/2 z-10 mb-1 -translate-x-1/2 whitespace-nowrap rounded-lg bg-ink px-2 py-1 text-xs text-white" dir="rtl">{{ b.from }} · {{ b.count === 1 ? 'פנייה אחת' : b.count + ' פניות' }}</span>
                        }
                      </span>
                    </button>
                  }
                </div>
                <span></span>
                <div class="mt-1 flex gap-1 text-[11px] tabular-nums text-muted">
                  @for (b of v.perHalfHour; track b.from; let i = $index) {
                    <span class="min-w-0 flex-1 text-center">{{ i % labelEvery() === 0 ? b.from : '' }}</span>
                  }
                </div>
              </div>
            }
          </section>

          <div class="grid gap-4 lg:grid-cols-3">
            <section class="card p-5">
              <h2 class="mb-3 text-lg font-semibold">נהגים עכשיו</h2>
              <dl class="grid grid-cols-[1fr_auto] gap-y-1.5">
                <dt>פעילים</dt><dd class="font-semibold tabular-nums">{{ v.drivers.active }}</dd>
                <dt>פנויים</dt><dd class="font-semibold tabular-nums">{{ v.drivers.free }}</dd>
                <dt>מתפנים</dt><dd class="font-semibold tabular-nums">{{ v.drivers.soon }}</dd>
                <dt>בנסיעה</dt><dd class="font-semibold tabular-nums">{{ v.drivers.onTrip }}</dd>
                <dt>בהפסקה</dt><dd class="font-semibold tabular-nums">{{ v.drivers.onBreak }}</dd>
              </dl>
            </section>
            <section class="card p-5">
              <h2 class="mb-3 text-lg font-semibold">סיבות ביטול</h2>
              @for (c of v.cancelReasons; track c.reason) {
                <div class="flex justify-between border-t border-line py-1.5 first:border-0"><span>{{ c.reason }}</span><b class="tabular-nums">{{ c.count }}</b></div>
              } @empty { <p class="text-muted">אין ביטולים היום.</p> }
            </section>
            <section class="card p-5">
              <h2 class="mb-3 text-lg font-semibold">פניות שקלט כל מוקדן היום</h2>
              @for (o of v.operators; track o.name) {
                <div class="flex justify-between border-t border-line py-1.5 first:border-0"><span>{{ o.name }}</span><b class="tabular-nums">{{ o.count }}</b></div>
              } @empty { <p class="text-muted">עוד לא נקלטו פניות היום.</p> }
            </section>
          </div>
        </div>
      }
    }
  `,
})
export class DashboardPage implements OnInit, OnDestroy {
  private readonly api = inject(Api);
  protected readonly d = signal<Dashboard | null>(null);
  protected readonly asTable = signal(false);
  protected readonly hover = signal<number | null>(null);
  private timer?: ReturnType<typeof setInterval>;

  protected readonly max = computed(() => Math.max(0, ...(this.d()?.perHalfHour ?? []).map((b) => b.count)));
  /**
   * Top of the scale: a fixed 50, so 30 and 40 requests look different and one request is a low bar.
   * Only a busier half hour raises it, in steps of 25.
   */
  protected readonly scaleMax = computed(() => Math.max(50, Math.ceil(this.max() / 25) * 25));
  /** A line every 5 requests, or more between lines when the scale is high (at most 10 lines). */
  protected readonly step = computed(() => [5, 10, 25, 50, 100, 250, 500].find((s) => this.scaleMax() / s <= 10) ?? 1000);
  protected readonly ticks = computed(() => Array.from({ length: Math.floor(this.scaleMax() / this.step()) + 1 }, (_, i) => i * this.step()));
  protected readonly labelEvery = computed(() => Math.max(1, Math.ceil((this.d()?.perHalfHour.length ?? 0) / 12)));
  protected readonly tiles = computed(() => {
    const v = this.d();
    if (!v?.active) return [];
    return [
      { label: 'פניות היום', value: v.requestsToday, unit: '', alert: false },
      { label: 'ממתינים בתור', value: v.queued, unit: '', alert: false },
      { label: 'המתנה ממוצעת', value: v.averageWait, unit: 'דקות', alert: false },
      { label: 'ההמתנה הארוכה', value: v.longestWait, unit: 'דקות', alert: v.longestWait > 30 },
      { label: 'הגיעו לקלפי', value: v.arrived, unit: '', alert: false },
      { label: 'בוטלו', value: v.cancelled, unit: '', alert: false },
    ];
  });

  async ngOnInit() {
    await this.load();
    this.timer = setInterval(() => this.load(true), 60_000);
  }

  ngOnDestroy() {
    clearInterval(this.timer);
  }

  private async load(background = false) {
    this.d.set(await this.api.get<Dashboard>('/api/city/dashboard', background));
  }
}
