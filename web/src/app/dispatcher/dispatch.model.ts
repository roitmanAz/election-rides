/** Shapes and rules of the dispatcher screen (spec 5.3 to 5.7). The car rules mirror the server's ProposalEngine. */

export type Kind = 'Men' | 'Women' | 'Family';
export type DriverStatus = 'Free' | 'Soon' | 'OnTrip' | 'Break' | 'Finished';

/** A route along the roads from our server (OSRM). */
export interface RoadRoute {
  points: LatLon[];
  km: number;
  minutes: number;
}

/** Straight-line estimate when no road route is available: distance x1.3 for the streets, at 25 km/h in town. */
export function straightEstimate(route: LatLon[]): { km: number; minutes: number } | null {
  if (route.length < 2) return null;
  const R = 6371;
  let km = 0;
  for (let i = 1; i < route.length; i++) {
    const a = route[i - 1];
    const b = route[i];
    const dLat = ((b.lat - a.lat) * Math.PI) / 180;
    const dLon = ((b.lon - a.lon) * Math.PI) / 180;
    const h = Math.sin(dLat / 2) ** 2 + Math.cos((a.lat * Math.PI) / 180) * Math.cos((b.lat * Math.PI) / 180) * Math.sin(dLon / 2) ** 2;
    km += 2 * R * Math.asin(Math.sqrt(h));
  }
  km *= 1.3;
  return { km: Math.round(km * 10) / 10, minutes: Math.max(1, Math.ceil((km / 25) * 60)) };
}

export interface LatLon {
  lat: number;
  lon: number;
}

export interface QueueRow {
  id: string;
  number: number;
  kind: Kind;
  waitMinutes: number;
  minutesToDeadline: number | null;
  deadlineSoon: boolean;
  notYet: boolean;
  /** Suspended by the dispatcher (far area): kept aside, offered only to a driver who starts near it. */
  suspended: boolean;
  passengers: { fullName: string; gender: 'Male' | 'Female'; clusterId: string; ballotBox: string | null }[];
  pending: boolean;
  callerPhone: string;
  street: string;
  buildingNumber: string;
  entrance: string | null;
  driverNotes: string | null;
  approximateLocation: boolean;
  location: LatLon | null;
  clusterIds: string[];
  needsWheelchair: boolean;
  needsWalker: boolean;
  /** Comes with an escort: one more seat. */
  withEscort: boolean;
  needsHelp: boolean;
  timeMode: 'Now' | 'Between' | 'LeaveBy';
  windowFrom: string | null;
  windowTo: string | null;
  urgencyReason: string | null;
  returnMode: 'DriverWaits' | 'CallsSeparately' | 'NoReturn';
  createdAt: string;
}

export interface TripStop {
  kind: 'Pickup' | 'DropOff';
  clusterId: string | null;
  doneAt: string | null;
  requestId: string | null;
  names: string[] | null;
  address: string | null;
  phone: string | null;
  status: string | null;
  /** Needs and the note for the driver, for the printed sheet. */
  notes?: string | null;
  returnsWithDriver?: boolean;
}

/** A waiting request that can join a ride already sent, right on the way (spec 7). */
export interface JoinOption {
  tripId: string;
  driverId: string;
  driverName: string;
}

/** A notice in the corner of the dispatcher's screen, for the few things that must not be missed. */
export interface Notice {
  key: string;
  text: string;
  requestId?: string;
  driverId?: string;
}

export interface DriverRow {
  id: string;
  fullName: string;
  phone: string;
  hasSmartphone: boolean;
  seats: number;
  wheelchairCar: boolean;
  status: DriverStatus;
  statusSince: string;
  freeAt: string | null;
  breakAfterTrip: boolean;
  minutesToFree: number | null;
  location: LatLon | null;
  hasLink: boolean;
  /** Optional area of the driver (street and number), the starting point when nothing newer is known. */
  areaStreetId: string | null;
  areaBuilding: string | null;
  lastDecline: string | null;
  trip: { id: string; status: 'Offered' | 'Active'; offeredAt: string; stops: TripStop[] } | null;
}

export interface ClusterRow {
  id: string;
  number: number | null;
  name: string;
  shortName: string;
  accessible: boolean;
  street: string;
  buildingNumber: string;
  dropoffNote: string | null;
  location: LatLon | null;
}

export const KIND_NAMES: Record<Kind, string> = { Men: 'גבר', Women: 'אישה', Family: 'משפחה' };

export const DRIVER_STATUS: Record<DriverStatus, string> = {
  Free: 'פנוי',
  Soon: 'מתפנה',
  OnTrip: 'בנסיעה',
  Break: 'בהפסקה',
  Finished: 'סיים',
};

/** Wait colors (DESIGN.md): up to 15 minutes green, 15 to 30 amber, over 30 red; a "leave by" within 30 minutes is red too. */
export function waitClass(r: QueueRow): 'ok' | 'warn' | 'bad' {
  if (r.deadlineSoon || r.waitMinutes >= 30) return 'bad';
  return r.waitMinutes >= 15 ? 'warn' : 'ok';
}

export function kindOf(r: QueueRow): string {
  if (r.kind === 'Family') return 'משפחה';
  const n = r.passengers.length;
  if (r.kind === 'Men') return n > 1 ? `${n} גברים` : 'גבר';
  return n > 1 ? `${n} נשים` : 'אישה';
}

/** Why a request cannot join the car; empty when it can (same words as the server). */
/** Seats a request takes: its passengers, and one more for an escort. */
export function seatsOf(r: Pick<QueueRow, 'passengers' | 'withEscort'>): number {
  return r.passengers.length + (r.withEscort ? 1 : 0);
}

export function whyNot(d: Pick<DriverRow, 'seats' | 'wheelchairCar'>, inCar: QueueRow[], r: QueueRow): string {
  if (inCar.some((x) => x.id === r.id)) return 'כבר בהצעה';
  if (r.needsWheelchair && !d.wheelchairCar) return 'הרכב לא נגיש';
  if (inCar.reduce((s, x) => s + seatsOf(x), 0) + seatsOf(r) > d.seats) return 'אין מקום ברכב';
  if (inCar.length) {
    if (r.kind === 'Family' || inCar[0].kind === 'Family') return 'משפחה נוסעת לבד';
    if (r.kind !== inCar[0].kind) return inCar[0].kind === 'Men' ? 'ברכב גברים' : 'ברכב נשים';
  }
  return '';
}

export function km(a: LatLon, b: LatLon): number {
  const R = 6371;
  const rad = (d: number) => (d * Math.PI) / 180;
  const dLat = rad(b.lat - a.lat);
  const dLon = rad(b.lon - a.lon);
  const h = Math.sin(dLat / 2) ** 2 + Math.cos(rad(a.lat)) * Math.cos(rad(b.lat)) * Math.sin(dLon / 2) ** 2;
  return 2 * R * Math.asin(Math.sqrt(h));
}

/** Read-aloud script for a driver without the app (spec 5.4): addresses in order, names, phones, notes. */
export function script(stops: QueueRow[], clusterName: (id: string) => string): string[] {
  const lines = stops.map((r, i) => {
    const addr = `${r.street} ${r.buildingNumber}${r.entrance ? ' כניסה ' + r.entrance : ''}`;
    const names = r.passengers.map((p) => p.fullName).join(' ו');
    const notes = [r.needsWheelchair && 'כיסא גלגלים', r.needsWalker && 'הליכון', r.needsHelp && 'צריך עזרה', r.withEscort && 'עם מלווה', r.driverNotes]
      .filter(Boolean)
      .join(', ');
    return `${i + 1}. ${addr}: ${names}, טלפון ${r.callerPhone}${notes ? '. ' + notes : ''}.`;
  });
  const dests = [...new Set(stops.flatMap((r) => r.clusterIds))].map(clusterName);
  lines.push(`ואז אל ${dests.join(', ')}.`);
  return lines;
}
