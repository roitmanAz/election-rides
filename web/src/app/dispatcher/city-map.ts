import { AfterViewInit, Component, ElementRef, OnDestroy, effect, input, output, viewChild } from '@angular/core';
import * as L from 'leaflet';
import { ClusterRow, DriverRow, LatLon, QueueRow, RoadRoute, waitClass } from './dispatch.model';

const WAIT_COLORS = { ok: '#1f7a3a', warn: '#c98a00', bad: '#c92a2a' } as const;

/**
 * The dispatcher's map (spec 10): requests colored by wait, drivers, cluster plates, and the selected
 * proposal's route along the roads (from our server's routing; a dashed straight line until it arrives or if
 * it is not available). OpenStreetMap during development; Google can replace the tiles before going live.
 * Zoom and pan with the mouse or the buttons.
 */
@Component({
  selector: 'app-city-map',
  // Its own stacking layer: Leaflet's high z-index values stay inside the map, so dialogs cover it.
  host: { class: 'relative isolate z-0 block h-full w-full' },
  template: `<div #box class="h-full min-h-[280px] w-full" [class.cursor-crosshair]="pickPoint()" role="img" aria-label="מפת העיר"></div>`,
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

  private readonly box = viewChild.required<ElementRef<HTMLElement>>('box');
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

  ngAfterViewInit() {
    this.map = L.map(this.box().nativeElement, { zoomControl: true, attributionControl: true }).setView([31.778, 35.216], 13);
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
      maxZoom: 19,
      attribution: '© OpenStreetMap',
    }).addTo(this.map);
    this.layer.addTo(this.map);
    this.map.on('click', (e: L.LeafletMouseEvent) => {
      if (this.pickPoint()) this.pointPicked.emit({ lat: e.latlng.lat, lon: e.latlng.lng });
    });
    this.draw(this.queue(), this.drivers(), this.clusters(), this.selectedId(), this.route(), this.road());
    // The box can change size with the layout; let the map follow.
    new ResizeObserver(() => this.map?.invalidateSize()).observe(this.box().nativeElement);
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
    for (const q of queue) {
      if (!q.location) continue;
      const sel = q.id === selected;
      L.circleMarker([q.location.lat, q.location.lon], {
        radius: sel ? 10 : 7, color: sel ? '#16a34a' : '#fff', weight: sel ? 4 : 2,
        fillColor: WAIT_COLORS[waitClass(q)], fillOpacity: 1, dashArray: q.approximateLocation ? '3 3' : undefined,
      })
        .bindTooltip(`פנייה ${q.number}: ${q.passengers.map((p) => p.fullName).join(', ')}`)
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

function initials(name: string) {
  return name.split(' ').filter(Boolean).map((w) => w[0]).slice(0, 2).join('');
}
function escape(s: string) {
  return s.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]!);
}
