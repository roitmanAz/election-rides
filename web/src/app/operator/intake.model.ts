/** Shapes and pure helpers for the operator screen (spec 5.1). Kept separate so they can be unit tested. */

export type Gender = 'Male' | 'Female';
export type TimeMode = 'Now' | 'Between' | 'LeaveBy';
export type ReturnMode = 'DriverWaits' | 'CallsSeparately' | 'NoReturn';
export type Status = 'Queued' | 'Assigned' | 'PickedUp' | 'Arrived' | 'Done' | 'Cancelled';

export const STATUS_NAMES: Record<Status, string> = {
  Queued: 'בתור',
  Assigned: 'שובץ',
  PickedUp: 'נאסף',
  Arrived: 'הגיע לקלפי',
  Done: 'נסגר',
  Cancelled: 'בוטל',
};
export const URGENCY_REASONS = ['יציאה מהעיר', 'טיפול רפואי', 'עבודה', 'אחר'];
export const CANCEL_REASONS = ['הפונה ביטל', 'מצא הסעה אחרת', 'פנייה כפולה', 'אחר'];

export interface ClusterRef {
  id: string;
  number: number | null;
  name: string;
  shortName: string;
  accessible: boolean;
  street: string;
  buildingNumber: string;
}

/** A passenger on the form: a known person picked from the phone list, or one typed in. */
export interface Passenger {
  key: string;
  personId: string | null;
  fullName: string;
  idNumber: string;
  noId: boolean;
  gender: Gender;
  clusterId: string;
  /** Known people from the phone list can be ticked on and off. */
  selected: boolean;
  fromPhone: boolean;
}

/** Phone rules (spec 5.1): dashes and spaces removed; 05/07 with 10 digits, 02/03/04/08/09 with 9. */
export function phoneDigits(v: string): string {
  return v.replace(/[\s-]/g, '');
}
export function phoneError(v: string): string {
  const d = phoneDigits(v);
  if (!d) return 'צריך למלא מספר טלפון';
  if (!/^\d+$/.test(d)) return 'רק ספרות';
  if (/^0[57]/.test(d)) return d.length === 10 ? '' : 'מספר שמתחיל ב-05 או 07 הוא 10 ספרות';
  if (/^0[23489]/.test(d)) return d.length === 9 ? '' : 'מספר נייח הוא 9 ספרות';
  return 'המספר צריך להתחיל ב-05, 07, 02, 03, 04, 08 או 09';
}
export function formatPhone(v: string): string {
  const d = phoneDigits(v);
  if (d.length === 10) return `${d.slice(0, 3)}-${d.slice(3)}`;
  if (d.length === 9) return `${d.slice(0, 2)}-${d.slice(2)}`;
  return d;
}

/** Israeli ID: up to 9 digits with a check digit. */
export function idValid(v: string): boolean {
  if (!/^\d{1,9}$/.test(v)) return false;
  const d = v.padStart(9, '0');
  let sum = 0;
  for (let i = 0; i < 9; i++) {
    let n = +d[i] * (i % 2 ? 2 : 1);
    if (n > 9) n -= 9;
    sum += n;
  }
  return sum % 10 === 0 && d !== '000000000';
}

export function clusterLabel(c: ClusterRef | undefined): string {
  if (!c) return '';
  return c.number != null ? `אשכול ${c.number}, ${c.name}` : c.name;
}

/** "רחל ושמואל כהן" style list: "a, b ו-c" becomes "a, b וc" as the prototype reads it. */
export function joinNames(names: string[]): string {
  if (names.length <= 1) return names.join('');
  return names.slice(0, -1).join(', ') + ' ו' + names[names.length - 1];
}

/** True when the selected passengers are of both genders: a family that travels alone (spec 7). */
export function isFamily(ps: Passenger[]): boolean {
  return new Set(ps.map((p) => p.gender)).size > 1;
}
