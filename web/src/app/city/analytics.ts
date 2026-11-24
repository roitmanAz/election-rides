import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../core/api';
import { Icon } from '../shared/icon';
import { Select, SelectOption, dayText } from '../shared/select';

interface Named { name: string }
interface Analytics {
  active: boolean;
  days: string[];
  totals: {
    requests: number; passengers: number; arrived: number; cancelled: number; queued: number;
    averageWait: number; longestWait: number; trips: number; passengersPerTrip: number; averageIntakeSeconds: number | null;
  };
  perHour: { hour: string; count: number }[];
  perDay: { day: string; count: number }[];
  waitBuckets: { label: string; count: number }[];
  operators: (Named & { requests: number; cancelled: number; averageIntakeSeconds: number | null })[];
  drivers: (Named & { trips: number; passengers: number; km: number })[];
  clusters: (Named & { number: number | null; requests: number })[];
  communities: (Named & { requests: number })[];
  needs: { wheelchair: number; walker: number; help: number };
  timeModes: { now: number; between: number; leaveBy: number };
  cancelReasons: { reason: string; count: number }[];
}

/** "12 דק' 30 שנ'" style, for intake times. */
function duration(sec: number | null): string {
  if (sec == null) return '—';
  const m = Math.floor(sec / 60);
  const s = sec % 60;
  return m ? `${m} דק' ${s ? s + " שנ'" : ''}`.trim() : `${s} שנ'`;
}

/**
 * City admin analytics (spec 3.1): the whole operation or one day. Numbers, requests per hour, waits, operators,
 * drivers, clusters, communities, needs, time requests and cancel reasons, and a file for Excel.
 */
@Component({
  selector: 'app-analytics',
  imports: [FormsModule, Icon, Select],
  template: `
    <div class="mb-4 flex flex-wrap items-center gap-3">
      <h1 class="text-2xl font-semibold">ניתוח נתונים</h1>
      @if (a()?.active) {
        <div class="ms-auto w-56"><app-select [options]="dayOptions()" [ngModel]="day()" (ngModelChange)="pick($event)" ariaLabel="איזה יום?" /></div>
        <a class="btn-secondary h-11 px-4" [href]="exportUrl()" download><app-icon name="list" [size]="18" /> הורדה לאקסל</a>
      }
    </div>

    @if (a(); as v) {
      @if (!v.active) {
        <div class="card p-6 text-muted">העיר לא משתתפת עכשיו במבצע בחירות פעיל.</div>
      } @else if (!v.totals.requests) {
        <div class="card p-6 text-muted">אין עדיין פניות {{ day() ? 'ביום הזה' : 'במבצע' }}.</div>
      } @else {
        <div class="grid gap-4">
          <!-- Numbers -->
          <div class="grid grid-cols-2 gap-3 sm:grid-cols-3 xl:grid-cols-5">
            @for (t of tiles(); track t.label) {
              <div class="card p-4">
                <p class="text-sm text-muted">{{ t.label }}</p>
                <p class="mt-1 text-2xl font-semibold tabular-nums">{{ t.value }}</p>
              </div>
            }
          </div>

          <!-- Requests per hour -->
          <section class="card p-5">
            <h2 class="mb-3 text-lg font-semibold">פניות לפי שעה{{ day() ? '' : ' (כל ימי המבצע יחד)' }}</h2>
            <div dir="ltr" class="grid grid-cols-[auto_minmax(0,1fr)] gap-x-2">
              <div class="relative h-44 w-7 text-[11px] tabular-nums text-muted">
                @for (t of ticks(); track t) {
                  <span class="absolute right-0 translate-y-1/2 leading-none" [style.bottom.%]="(t / scaleMax()) * 100">{{ t }}</span>
                }
              </div>
              <div class="relative flex h-44 items-end gap-1 border-b border-line">
                @for (t of ticks(); track t) {
                  @if (t > 0) { <span class="pointer-events-none absolute inset-x-0 border-t border-line" [style.bottom.%]="(t / scaleMax()) * 100"></span> }
                }
                @for (b of hours(); track b.hour; let i = $index) {
                  <button type="button" class="relative flex h-full min-w-0 flex-1 items-end focus:outline-none"
                    [attr.aria-label]="b.hour + ': ' + b.count + ' פניות'" (mouseenter)="hover.set(i)" (mouseleave)="hover.set(null)" (focus)="hover.set(i)" (blur)="hover.set(null)">
                    <span class="relative mx-auto block w-full max-w-12 rounded-t-[4px] bg-action" [style.height.%]="(b.count / scaleMax()) * 100"
                      [class.opacity-70]="hover() !== null && hover() !== i">
                      @if (hover() === i) {
                        <span class="absolute bottom-full left-1/2 z-10 mb-1 -translate-x-1/2 whitespace-nowrap rounded-lg bg-ink px-2 py-1 text-xs text-white" dir="rtl">{{ b.hour }} · {{ b.count === 1 ? 'פנייה אחת' : b.count + ' פניות' }}</span>
                      }
                    </span>
                  </button>
                }
              </div>
              <span></span>
              <div class="mt-1 flex gap-1 text-[11px] tabular-nums text-muted">
                @for (b of hours(); track b.hour) { <span class="min-w-0 flex-1 text-center">{{ b.hour }}</span> }
              </div>
            </div>
          </section>

          <div class="grid gap-4 lg:grid-cols-2">
            <section class="card p-5">
              <h2 class="mb-3 text-lg font-semibold">המתנה עד שיבוץ</h2>
              @for (b of v.waitBuckets; track b.label) {
                <div class="grid grid-cols-[110px_minmax(0,1fr)_40px] items-center gap-2 py-1 text-sm">
                  <span>{{ b.label }}</span>
                  <span class="h-3 rounded-full bg-soft"><span class="block h-3 rounded-full bg-action" [style.width.%]="pct(b.count, waitMax())"></span></span>
                  <b class="text-end tabular-nums">{{ b.count }}</b>
                </div>
              }
            </section>
            @if (!day() && v.perDay.length > 1) {
              <section class="card p-5">
                <h2 class="mb-3 text-lg font-semibold">פניות לפי יום</h2>
                @for (d of v.perDay; track d.day) {
                  <div class="grid grid-cols-[110px_minmax(0,1fr)_40px] items-center gap-2 py-1 text-sm">
                    <span class="tabular-nums">{{ dayText(d.day) }}</span>
                    <span class="h-3 rounded-full bg-soft"><span class="block h-3 rounded-full bg-action" [style.width.%]="pct(d.count, dayMax())"></span></span>
                    <b class="text-end tabular-nums">{{ d.count }}</b>
                  </div>
                }
              </section>
            }
            <section class="card p-5">
              <h2 class="mb-3 text-lg font-semibold">צרכים מיוחדים וזמנים</h2>
              <dl class="grid grid-cols-[1fr_auto] gap-y-1.5 text-sm">
                <dt>כיסא גלגלים</dt><dd class="font-semibold tabular-nums">{{ v.needs.wheelchair }}</dd>
                <dt>הליכון</dt><dd class="font-semibold tabular-nums">{{ v.needs.walker }}</dd>
                <dt>צריך עזרה</dt><dd class="font-semibold tabular-nums">{{ v.needs.help }}</dd>
                <dt class="mt-2 border-t border-line pt-2">עכשיו</dt><dd class="mt-2 border-t border-line pt-2 font-semibold tabular-nums">{{ v.timeModes.now }}</dd>
                <dt>בין שעות</dt><dd class="font-semibold tabular-nums">{{ v.timeModes.between }}</dd>
                <dt>לצאת עד</dt><dd class="font-semibold tabular-nums">{{ v.timeModes.leaveBy }}</dd>
              </dl>
            </section>
            <section class="card p-5">
              <h2 class="mb-3 text-lg font-semibold">סיבות ביטול</h2>
              @for (c of v.cancelReasons; track c.reason) {
                <div class="flex justify-between border-t border-line py-1.5 text-sm first:border-0"><span>{{ c.reason }}</span><b class="tabular-nums">{{ c.count }}</b></div>
              } @empty { <p class="text-sm text-muted">אין ביטולים.</p> }
            </section>
          </div>

          <!-- Tables -->
          <div class="grid gap-4 xl:grid-cols-2">
            <section class="card overflow-x-auto p-5">
              <h2 class="mb-3 text-lg font-semibold">מוקדנים</h2>
              <table class="w-full text-sm">
                <thead><tr class="text-muted"><th class="py-1.5 text-start font-medium">שם</th><th class="py-1.5 text-start font-medium">פניות</th><th class="py-1.5 text-start font-medium">בוטלו</th><th class="py-1.5 text-start font-medium">זמן קליטה ממוצע</th></tr></thead>
                <tbody>@for (o of v.operators; track o.name) {
                  <tr class="border-t border-line"><td class="py-1.5">{{ o.name }}</td><td class="py-1.5 tabular-nums">{{ o.requests }}</td><td class="py-1.5 tabular-nums">{{ o.cancelled }}</td><td class="py-1.5 tabular-nums">{{ duration(o.averageIntakeSeconds) }}</td></tr>
                }</tbody>
              </table>
            </section>
            <section class="card overflow-x-auto p-5">
              <h2 class="mb-3 text-lg font-semibold">נהגים</h2>
              @if (v.drivers.length) {
                <table class="w-full text-sm">
                  <thead><tr class="text-muted"><th class="py-1.5 text-start font-medium">שם</th><th class="py-1.5 text-start font-medium">נסיעות</th><th class="py-1.5 text-start font-medium">נוסעים</th><th class="py-1.5 text-start font-medium">ק"מ (משוער)</th></tr></thead>
                  <tbody>@for (d of v.drivers; track d.name) {
                    <tr class="border-t border-line"><td class="py-1.5">{{ d.name }}</td><td class="py-1.5 tabular-nums">{{ d.trips }}</td><td class="py-1.5 tabular-nums">{{ d.passengers }}</td><td class="py-1.5 tabular-nums">{{ d.km }}</td></tr>
                  }</tbody>
                </table>
              } @else { <p class="text-sm text-muted">עוד אין נסיעות.</p> }
            </section>
            <section class="card overflow-x-auto p-5">
              <h2 class="mb-3 text-lg font-semibold">אשכולות</h2>
              <table class="w-full text-sm">
                <thead><tr class="text-muted"><th class="py-1.5 text-start font-medium">אשכול</th><th class="py-1.5 text-start font-medium">פניות</th></tr></thead>
                <tbody>@for (c of v.clusters; track c.name) {
                  <tr class="border-t border-line"><td class="py-1.5">{{ c.number != null ? c.number + ' · ' : '' }}{{ c.name }}</td><td class="py-1.5 tabular-nums">{{ c.requests }}</td></tr>
                }</tbody>
              </table>
            </section>
            <section class="card overflow-x-auto p-5">
              <h2 class="mb-3 text-lg font-semibold">קהילות</h2>
              <table class="w-full text-sm">
                <thead><tr class="text-muted"><th class="py-1.5 text-start font-medium">קהילה</th><th class="py-1.5 text-start font-medium">פניות</th></tr></thead>
                <tbody>@for (c of v.communities; track c.name) {
                  <tr class="border-t border-line"><td class="py-1.5">{{ c.name }}</td><td class="py-1.5 tabular-nums">{{ c.requests }}</td></tr>
                }</tbody>
              </table>
            </section>
          </div>
          <p class="text-sm text-muted">ההמתנה נמדדת מתחילת ההמתנה (שמירת הפנייה, או תחילת חלון האיסוף) ועד ששובץ נהג. הקילומטרים משוערים לפי קו אווירי בין העצירות, פי 1.3. זמן הקליטה נמדד רק בפניות חדשות, מהקלדת הטלפון ועד השמירה. בהורדה לאקסל אין שמות, טלפונים או תעודות זהות של פונים.</p>
        </div>
      }
    }
  `,
})
export class AnalyticsPage implements OnInit {
  private readonly api = inject(Api);
  protected readonly a = signal<Analytics | null>(null);
  protected readonly day = signal<string>('');
  protected readonly hover = signal<number | null>(null);
  protected readonly dayText = dayText;
  protected readonly duration = duration;

  protected readonly dayOptions = computed<SelectOption[]>(() => [
    { value: '', label: 'כל המבצע' },
    ...(this.a()?.days ?? []).map((d) => ({ value: d, label: dayText(d) })),
  ]);
  protected readonly exportUrl = computed(() => '/api/city/analytics/export' + (this.day() ? '?day=' + this.day() : ''));

  protected readonly tiles = computed(() => {
    const t = this.a()?.totals;
    if (!t) return [];
    return [
      { label: 'פניות', value: t.requests },
      { label: 'נוסעים', value: t.passengers },
      { label: 'הגיעו לקלפי', value: t.arrived },
      { label: 'בוטלו', value: t.cancelled },
      { label: 'עדיין בתור', value: t.queued },
      { label: 'המתנה ממוצעת עד שיבוץ', value: t.averageWait + " דק'" },
      { label: 'ההמתנה הארוכה עד שיבוץ', value: t.longestWait + " דק'" },
      { label: 'נסיעות', value: t.trips },
      { label: 'נוסעים בנסיעה, בממוצע', value: t.passengersPerTrip },
      { label: 'זמן קליטה ממוצע', value: duration(t.averageIntakeSeconds) },
    ];
  });

  /** Every hour from the first to the last with requests, so empty hours show as gaps. */
  protected readonly hours = computed(() => {
    const list = this.a()?.perHour ?? [];
    if (!list.length) return [];
    const first = +list[0].hour.slice(0, 2);
    const last = +list[list.length - 1].hour.slice(0, 2);
    return Array.from({ length: last - first + 1 }, (_, i) => {
      const hour = `${String(first + i).padStart(2, '0')}:00`;
      return { hour, count: list.find((b) => b.hour === hour)?.count ?? 0 };
    });
  });
  /** Same scale as the dashboard: a fixed 50, raised in steps of 25 only when needed; a line every 5 (at most 10 lines). */
  protected readonly scaleMax = computed(() => Math.max(50, Math.ceil(Math.max(0, ...this.hours().map((b) => b.count)) / 25) * 25));
  protected readonly step = computed(() => [5, 10, 25, 50, 100, 250, 500].find((s) => this.scaleMax() / s <= 10) ?? 1000);
  protected readonly ticks = computed(() => Array.from({ length: Math.floor(this.scaleMax() / this.step()) + 1 }, (_, i) => i * this.step()));
  protected readonly waitMax = computed(() => Math.max(1, ...(this.a()?.waitBuckets ?? []).map((b) => b.count)));
  protected readonly dayMax = computed(() => Math.max(1, ...(this.a()?.perDay ?? []).map((b) => b.count)));

  protected pct(n: number, max: number) {
    return (n / max) * 100;
  }

  async ngOnInit() {
    await this.load();
  }

  protected async pick(day: string) {
    this.day.set(day ?? '');
    await this.load();
  }

  private async load() {
    this.a.set(await this.api.get<Analytics>('/api/city/analytics' + (this.day() ? '?day=' + this.day() : '')));
  }
}
