import { AfterViewInit, Component, ElementRef, OnDestroy, effect, input, output, signal, viewChild } from '@angular/core';
import * as L from 'leaflet';
// A vector-map base layer that lives inside Leaflet, so all the Leaflet markers and routes stay as they are.
import '@maplibre/maplibre-gl-leaflet';
import { ClusterRow, DriverRow, LatLon, QueueRow, RoadRoute, waitClass } from './dispatch.model';
import { Icon } from '../shared/icon';

const WAIT_COLORS = { ok: '#1f7a3a', warn: '#c98a00', bad: '#c92a2a' } as const;

/**
 * The dispatcher's map (spec 10): requests colored by wait, drivers, cluster plates, and the selected
 * proposal's route along the roads (from our server's routing; a dashed straight line until it arrives or if
 * it is not available). OpenStreetMap by default; the tile source is a server config value, so it can be switched when a provider blocks.
 * Zoom and pan with the mouse or the buttons. A request is a pin whose tip is on the address, so the house
 * number on the map stays readable; marks get smaller when the map is zoomed out. Buttons sit on the map
 * itself (fit, enlarge) and a legend opens from the bottom corner.
 */
@Component({
  selector: 'app-city-map',
  // Its own stacking layer: Leaflet's high z-index values stay inside the map, so dialogs cover it.
  host: { class: 'relative isolate z-0 block h-full w-full', '(document:click)': 'outside($event)' },
  imports: [Icon],
  template: `
    <div #box class="h-full min-h-[280px] w-full" [class.cursor-crosshair]="pickPoint()" [class.map-far]="far()" role="img" aria-label="מפת העיר"></div>
    <div class="absolute right-2.5 top-2.5 z-[1001] flex flex-col gap-1.5">
      <button type="button" class="map-btn" title="הצג את כל הפניות" aria-label="הצג את כל הפניות" (click)="fit()"><app-icon name="fit" [size]="18" /></button>
      @if (canExpand()) {
        <button type="button" class="map-btn" title="הגדל מפה" aria-label="הגדל מפה" (click)="expand.emit()"><app-icon name="expand" [size]="18" /></button>
      }
    </div>
    <div class="absolute bottom-2.5 left-2.5 z-[1001] flex flex-col items-end gap-1.5" dir="ltr">
      @if (legendOpen()) {
        <div #legend dir="rtl" class="w-56 rounded-2xl bg-surface p-3 text-xs shadow-[var(--shadow-pop)]" role="region" aria-label="מקרא למפה">
          <p class="mb-1.5 font-semibold">פניות (לפי זמן המתנה)</p>
          <ul class="grid gap-1">
            <li class="flex items-center gap-2"><span class="legend-pin" style="background:#1f7a3a"></span>עד 15 דקות</li>
            <li class="flex items-center gap-2"><span class="legend-pin" style="background:#c98a00"></span>15 עד 30 דקות</li>
            <li class="flex items-center gap-2"><span class="legend-pin" style="background:#c92a2a"></span>30 דקות ומעלה, או חלון זמן שנגמר בקרוב</li>
            <li class="flex items-center gap-2"><span class="legend-pin legend-pin-sel" style="background:#c92a2a"></span>הפנייה הפתוחה</li>
            <li class="flex items-center gap-2"><span class="legend-pin legend-pin-approx" style="background:#1f7a3a"></span>מיקום משוער (מרכז הרחוב)</li>
          </ul>
          <p class="mb-1.5 mt-2.5 font-semibold">נהגים ומקומות</p>
          <ul class="grid gap-1">
            <li class="flex items-center gap-2"><span class="map-car !transform-none"></span>נהג פנוי</li>
            <li class="flex items-center gap-2"><span class="map-car map-car-Soon !transform-none"></span>מתפנה בקרוב</li>
            <li class="flex items-center gap-2"><span class="map-car map-car-OnTrip !transform-none"></span>בנסיעה או בהפסקה</li>
            <li class="flex items-center gap-2"><span class="map-plate !transform-none">12</span>אשכול קלפיות (מספר או שם)</li>
            <li class="flex items-center gap-2"><span class="inline-block h-1 w-6 rounded bg-action"></span>מסלול ההצעה על הכבישים</li>
            <li class="flex items-center gap-2"><span class="inline-block w-6 border-t-2 border-dashed border-action"></span>מסלול משוער (קו אווירי)</li>
          </ul>
        </div>
      }
      <button #legendBtn type="button" data-legend class="map-btn" [class.!bg-ink]="legendOpen()" [class.!text-white]="legendOpen()"
        title="מקרא" aria-label="מקרא למפה" [attr.aria-expanded]="legendOpen()" (click)="legendOpen.set(!legendOpen())"><app-icon name="info" [size]="18" /></button>
    </div>
  `,
})
export class CityMap implements AfterViewInit, OnDestroy {
  readonly queue = input<QueueRow[]>([]);
  readonly drivers = input<DriverRow[]>([]);
  readonly clusters = input<ClusterRow[]>([]);
  readonly selectedId = input<string | null>(null);
  /** The route of the selected proposal: points in stop order. */
  readonly route = input<LatLon[]>([]);
  readonly picked = output<string>();
  /** While on, a click on the map reports the point (for marking a pickup by hand). */
  readonly pickPoint = input(false);
  readonly pointPicked = output<LatLon>();
  /** Shows the "enlarge" button on the map; off inside the big map. */
  readonly canExpand = input(true);
  readonly expand = output<void>();
  protected readonly legendOpen = signal(false);
  /** Zoomed out: marks drawn smaller. */
  protected readonly far = signal(false);
  private zoom = 13;

  private readonly box = viewChild.required<ElementRef<HTMLElement>>('box');
  private readonly legendEl = viewChild<ElementRef<HTMLElement>>('legend');
  private readonly legendBtn = viewChild<ElementRef<HTMLElement>>('legendBtn');
  private map?: L.Map;
  private layer = L.layerGroup();
  private fitted = false;
  /** The road route of the selected proposal, when the screen has it. */
  readonly road = input<RoadRoute | null>(null);

  constructor() {
    effect(() => {
      this.draw(this.queue(), this.drivers(), this.clusters(), this.selectedId(), this.route(), this.road());
    });
  }

  async ngAfterViewInit() {
    this.map = L.map(this.box().nativeElement, { zoomControl: true, attributionControl: true }).setView([31.778, 35.216], 13);
    // The map background comes from the server config, so it can be switched without a code change.
    // A vector style (styleUrl) renders through MapLibre with Hebrew labels; otherwise plain image tiles (tileUrl).
    const cfg = await fetch('/api/config').then((r) => r.json()).catch(() => null);
    let glOk = false;
    // The plugin adds maplibreGL to Leaflet's live object; `import * as L` is a snapshot without it, so reach the
    // live object through `default`.
    type GlFactory = (o: object) => L.Layer & { getMaplibreMap(): MaplibreMap };
    const lib = L as unknown as { maplibreGL?: GlFactory; default?: { maplibreGL?: GlFactory } };
    const maplibreGL = lib.maplibreGL ?? lib.default?.maplibreGL;
    if (cfg?.styleUrl && maplibreGL) {
      try {
        const gl = maplibreGL({ style: cfg.styleUrl, attribution: cfg.tileAttribution ?? '© MapTiler © OpenStreetMap' });
        gl.addTo(this.map);
        hebrewLabels(gl.getMaplibreMap());
        glOk = true;
      } catch (e) {
        console.error('vector map failed, using image tiles:', e);
      }
    }
    if (!glOk) {
      L.tileLayer(cfg?.tileUrl ?? 'https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
        maxZoom: 19,
        attribution: cfg?.tileAttribution ?? '© OpenStreetMap',
      }).addTo(this.map);
    }
    this.layer.addTo(this.map);
    // Marks follow the zoom: smaller when zoomed out.
    this.map.on('zoomend', () => {
      const z = this.map!.getZoom();
      if (z === this.zoom) return;
      this.zoom = z;
      this.far.set(z <= 13);
      this.draw(this.queue(), this.drivers(), this.clusters(), this.selectedId(), this.route(), this.road());
    });
    this.map.on('click', (e: L.LeafletMouseEvent) => {
      if (this.pickPoint()) this.pointPicked.emit({ lat: e.latlng.lat, lon: e.latlng.lng });
    });
    this.draw(this.queue(), this.drivers(), this.clusters(), this.selectedId(), this.route(), this.road());
    // The box can change size with the layout; let the map follow.
    new ResizeObserver(() => this.map?.invalidateSize()).observe(this.box().nativeElement);
  }

  /** A click outside the legend (and not on its button) closes it. */
  protected outside(e: MouseEvent) {
    if (!this.legendOpen()) return;
    const t = e.target as Node;
    if (this.legendEl()?.nativeElement.contains(t) || this.legendBtn()?.nativeElement.contains(t)) return;
    this.legendOpen.set(false);
  }

  ngOnDestroy() {
    this.map?.remove();
  }

  /** Zooms to the given points, e.g. the proposal's route after a stop was added. */
  fitTo(points: LatLon[]) {
    if (this.map && points.length > 1)
      this.map.fitBounds(L.latLngBounds(points.map((p) => [p.lat, p.lon] as L.LatLngTuple)), { padding: [40, 40], maxZoom: 16 });
  }

  /** Re-fit to all points (the "reset" button). */
  fit() {
    const pts = [...this.queue().map((q) => q.location), ...this.clusters().map((c) => c.location)].filter(Boolean) as LatLon[];
    if (this.map && pts.length) this.map.fitBounds(L.latLngBounds(pts.map((p) => [p.lat, p.lon] as L.LatLngTuple)), { padding: [30, 30], maxZoom: 16 });
  }

  private draw(queue: QueueRow[], drivers: DriverRow[], clusters: ClusterRow[], selected: string | null, route: LatLon[],
    road: RoadRoute | null) {
    if (!this.map) return;
    this.layer.clearLayers();
    for (const c of clusters) {
      if (!c.location) continue;
      const label = c.number != null ? String(c.number) : c.shortName;
      L.marker([c.location.lat, c.location.lon], {
        icon: L.divIcon({ className: '', html: `<span class="map-plate">${escape(label)}</span>`, iconSize: undefined }),
        title: `${c.name}, ${c.street} ${c.buildingNumber}`,
      }).addTo(this.layer);
    }
    if (route.length > 1) {
      if (road)
        L.polyline(road.points.map((p) => [p.lat, p.lon] as L.LatLngTuple), { color: '#16a34a', weight: 5, opacity: 0.9 })
          // "auto" opens the label on the side with room, so the map's edge never cuts it.
          .bindTooltip(`${road.km} ק"מ על הכבישים, כ-${road.minutes} דקות נסיעה`, { sticky: true, direction: 'auto' })
          .addTo(this.layer);
      else
        L.polyline(route.map((p) => [p.lat, p.lon] as L.LatLngTuple), { color: '#16a34a', weight: 4, opacity: 0.7, dashArray: '6 8' }).addTo(this.layer);
    }
    // Pin size by zoom: full near the street, smaller as the map shows more of the city.
    const scale = this.zoom >= 15 ? 1 : this.zoom >= 13 ? 0.75 : 0.55;
    for (const q of queue) {
      if (!q.location) continue;
      const sel = q.id === selected;
      const w = Math.round((sel ? 26 : 20) * scale);
      const h = Math.round(w * 1.4);
      L.marker([q.location.lat, q.location.lon], {
        icon: L.divIcon({
          className: '',
          html: pinSvg(WAIT_COLORS[waitClass(q)], sel, q.approximateLocation, w, h),
          iconSize: [w, h],
          // The tip of the pin is on the address; the pin itself stands above it, beside the house number.
          iconAnchor: [w / 2, h],
          tooltipAnchor: [0, -h * 0.6],
        }),
        zIndexOffset: sel ? 1000 : 0,
        keyboard: false,
      })
        .bindTooltip(`פנייה ${q.number}: ${q.passengers.map((p) => p.fullName).join(', ')}, ${q.street} ${q.buildingNumber}${q.entrance ? ', כניסה ' + q.entrance : ''}`)
        .on('click', () => this.picked.emit(q.id))
        .addTo(this.layer);
    }
    for (const d of drivers) {
      if (!d.location) continue;
      L.marker([d.location.lat, d.location.lon], {
        icon: L.divIcon({ className: '', html: `<span class="map-car map-car-${d.status}">${escape(initials(d.fullName))}</span>` }),
        title: d.fullName,
      }).addTo(this.layer);
    }
    if (!this.fitted && (queue.some((q) => q.location) || clusters.some((c) => c.location))) {
      this.fitted = true;
      this.fit();
    }
  }
}

/** A map pin: a drop shape with a white dot, the tip at the bottom. Selected: a green ring; approximate: dashed. */
function pinSvg(fill: string, selected: boolean, approximate: boolean, w: number, h: number) {
  const stroke = selected ? '#16a34a' : '#fff';
  const sw = selected ? 3.5 : 2;
  const dash = approximate ? ' stroke-dasharray="3 2.5"' : '';
  return `<svg width="${w}" height="${h}" viewBox="0 0 20 28" style="display:block;filter:drop-shadow(0 1px 1.5px rgba(0,0,0,.35))">`
    + `<path d="M10 27C10 27 1.5 17 1.5 10a8.5 8.5 0 0 1 17 0C18.5 17 10 27 10 27z" fill="${fill}" stroke="${stroke}" stroke-width="${sw}"${dash}/>`
    + `<circle cx="10" cy="10" r="3.2" fill="#fff"/></svg>`;
}

function initials(name: string) {
  return name.split(' ').filter(Boolean).map((w) => w[0]).slice(0, 2).join('');
}
function escape(s: string) {
  return s.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]!);
}

/** The little of MapLibre's map that we touch, to avoid a full type dependency. */
interface MaplibreMap {
  isStyleLoaded(): boolean;
  once(ev: string, cb: () => void): void;
  getStyle(): { layers: { id: string; type: string; layout?: Record<string, unknown> }[] };
  setLayoutProperty(layer: string, prop: string, value: unknown): void;
}

/**
 * Shows place and street labels in Hebrew (the map provider's styles default to English). Each label layer whose
 * text comes from the "name" field is switched to the Hebrew name, falling back to the Latin name and then the
 * default; other labels (house numbers, road numbers) are left as they are.
 */
function hebrewLabels(map: MaplibreMap) {
  const apply = () => {
    const hebrew = ['coalesce', ['get', 'name:he'], ['get', 'name:latin'], ['get', 'name']];
    for (const layer of map.getStyle().layers) {
      if (layer.type !== 'symbol') continue;
      const field = JSON.stringify(layer.layout?.['text-field'] ?? '');
      // Only the layers that label by name, so house numbers and road shields are untouched.
      if (field.includes('name') && !field.includes('housenumber') && !field.includes('ref')) {
        try {
          map.setLayoutProperty(layer.id, 'text-field', hebrew);
        } catch {
          // A layer that will not take the expression keeps its own label.
        }
      }
    }
  };
  if (map.isStyleLoaded()) apply();
  else map.once('styledata', apply);
}
