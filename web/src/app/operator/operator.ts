import { Component, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { Api } from '../core/api';
import { AuthService, errorMessage } from '../core/auth.service';
import { Combo, ComboItem } from '../shared/combo';
import { Dialog } from '../shared/dialog';
import { Icon } from '../shared/icon';
import { RoleSwitch } from '../shared/role-switch';
import { Select, textOptions } from '../shared/select';
import {
  CANCEL_REASONS, ClusterRef, Gender, Passenger, ReturnMode, STATUS_NAMES, Status, TimeMode, URGENCY_REASONS,
  clusterLabel, formatPhone, idValid, isFamily, joinNames, phoneDigits, phoneError,
} from './intake.model';

interface SearchHit {
  id: string;
  number: number;
  callerPhone: string;
  status: Status;
  createdAt: string;
  cancelReason: string | null;
  passengers: { fullName: string; idNumber: string | null; cluster: { number: number | null; shortName: string; name: string } | null }[];
}
interface Mine {
  recent: { id: string; number: number; createdAt: string; status: Status; names: string[]; clusterId: string }[];
  mineToday: number;
  queued: number;
}

const EDITABLE: Status[] = ['Queued', 'Assigned'];
let keySeq = 0;

/**
 * Operator screen (spec 5.1, prototype docs/operator.html): four steps (phone, passengers, pickup address,
 * time and return), a summary to read back, then save into the queue. Status search in the top bar.
 */
@Component({
  selector: 'app-operator',
  imports: [FormsModule, Combo, Dialog, Icon, DatePipe, RoleSwitch, Select],
  templateUrl: './operator.html',
})
export class OperatorPage implements OnInit {
  private readonly api = inject(Api);
  protected readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  protected readonly statusNames = STATUS_NAMES;
  protected readonly urgencyReasons = URGENCY_REASONS;
  protected readonly cancelReasons = CANCEL_REASONS;
  protected readonly urgencyOpts = textOptions(URGENCY_REASONS);
  protected readonly cancelOpts = textOptions(CANCEL_REASONS);
  protected readonly editable = EDITABLE;
  protected readonly clusterLabel = clusterLabel;
  protected readonly formatPhone = formatPhone;

  // Lists of the city
  protected readonly clusters = signal<ClusterRef[]>([]);
  protected readonly clusterItems = signal<ComboItem[]>([]);
  protected readonly accessibleItems = signal<ComboItem[]>([]);
  protected readonly streetItems = signal<ComboItem[]>([]);
  protected readonly communityItems = signal<ComboItem[]>([]);
  protected readonly mine = signal<Mine | null>(null);

  // Step 1: phone
  protected phone = '';
  protected readonly phoneErr = signal('');
  protected readonly phoneChecked = signal('');
  protected readonly openOnPhone = signal<{ id: string; number: number }[]>([]);

  // Step 2: passengers
  protected readonly passengers = signal<Passenger[]>([]);
  protected readonly peopleCollapsed = signal(false);
  protected readonly newOpen = signal(false);
  protected np = { fullName: '', idNumber: '', noId: false, gender: 'Male' as Gender, clusterId: null as string | null };
  protected readonly npErr = signal<{ name?: string; id?: string; cluster?: string }>({});
  protected readonly editingKey = signal<string | null>(null);
  protected communityId: string | null = null;
  protected needsWheelchair = false;
  protected needsWalker = false;
  protected needsHelp = false;
  protected accAnswer: 'stay' | 'move' | null = null;
  protected accMoveTo: string | null = null;

  // Step 3: address
  protected streetId: string | null = null;
  protected building = '';
  protected entrance = '';
  protected notes = '';
  protected readonly streetErr = signal('');
  protected readonly buildingErr = signal('');

  // Step 4: time and return
  protected timeMode: TimeMode = 'Now';
  protected windowFrom = '13:00';
  protected windowTo = '15:00';
  protected leaveBy = '13:00';
  protected urgency = '';
  protected urgencyOther = '';
  protected returnMode: ReturnMode = 'DriverWaits';

  // Save, duplicates, edit mode
  protected readonly saving = signal(false);
  protected readonly saveError = signal('');
  protected readonly duplicate = signal<{ id: string; number: number } | null>(null);
  protected readonly editing = signal<{ id: string; number: number } | null>(null);
  protected readonly toast = signal('');

  // Search and cancel
  protected query = '';
  protected readonly hits = signal<SearchHit[] | null>(null);
  protected readonly cancelOf = signal<SearchHit | null>(null);
  protected cancelReason = '';
  protected cancelOther = '';
  protected readonly cancelErr = signal('');
  private searchSeq = 0;

  async ngOnInit() {
    const [clusters, streets, communities] = await Promise.all([
      this.api.get<ClusterRef[]>('/api/city/clusters'),
      this.api.get<{ id: string; name: string }[]>('/api/city/streets'),
      this.api.get<{ id: string; name: string }[]>('/api/city/communities'),
    ]);
    this.clusters.set(clusters);
    const item = (c: ClusterRef): ComboItem => ({ id: c.id, label: clusterLabel(c), sub: `${c.street} ${c.buildingNumber}` });
    this.clusterItems.set(clusters.map(item));
    this.accessibleItems.set(clusters.filter((c) => c.accessible).map(item));
    this.streetItems.set(streets.map((s) => ({ id: s.id, label: s.name })));
    this.communityItems.set(communities.map((c) => ({ id: c.id, label: c.name })));
    await this.loadMine();
  }

  private async loadMine() {
    this.mine.set(await this.api.get<Mine>('/api/intake/mine'));
  }

  protected cluster(id: string | null | undefined) {
    return this.clusters().find((c) => c.id === id);
  }

  // ---------- Step 1 ----------
  protected onPhoneInput() {
    if (this.phoneErr()) this.phoneErr.set(phoneError(this.phone));
    const d = phoneDigits(this.phone);
    if (!phoneError(this.phone) && d !== this.phoneChecked()) this.lookupPhone();
  }

  protected onPhoneBlur() {
    this.phoneErr.set(phoneError(this.phone));
  }

  private async lookupPhone() {
    const d = phoneDigits(this.phone);
    this.phoneChecked.set(d);
    const r = await this.api.get<{
      people: { personId: string | null; fullName: string; idNumber: string | null; gender: Gender | null; clusterId: string | null; ballotBox: string | null; age: number | null }[];
      openRequests: { id: string; number: number }[];
    }>(`/api/intake/phone/${d}`);
    this.openOnPhone.set(this.editing() ? r.openRequests.filter((o) => o.id !== this.editing()!.id) : r.openRequests);
    const typed = this.passengers().filter((p) => !p.fromPhone);
    const known = r.people
      .filter((p) => !typed.some((t) => (p.personId && t.personId === p.personId) || (p.idNumber && t.idNumber === p.idNumber)))
      .map<Passenger>((p) => ({
        key: 'k' + ++keySeq, personId: p.personId, fullName: p.fullName, idNumber: p.idNumber ?? '', noId: !p.idNumber,
        gender: p.gender, clusterId: p.clusterId ?? '', selected: false, fromPhone: true, ballotBox: p.ballotBox, age: p.age,
      }));
    this.passengers.set([...known, ...typed]);
    this.newOpen.set(known.length === 0 && typed.length === 0);
    this.peopleCollapsed.set(false);
  }

  protected phoneOk() {
    return !phoneError(this.phone) && phoneDigits(this.phone) === this.phoneChecked();
  }

  // ---------- Step 2 ----------
  protected selected() {
    return this.passengers().filter((p) => p.selected);
  }

  protected toggle(p: Passenger) {
    this.passengers.update((list) => list.map((x) => (x.key === p.key ? { ...x, selected: !x.selected } : x)));
    this.duplicate.set(null);
  }

  protected setGender(p: Passenger, gender: Gender) {
    this.passengers.update((list) => list.map((x) => (x.key === p.key ? { ...x, gender } : x)));
  }

  /** With a voter base, a typed ID is looked up: name, real ballot box and cluster are filled in (spec 5.1). */
  protected readonly baseNote = signal('');
  protected async lookupId() {
    this.baseNote.set('');
    const id = this.np.idNumber.trim();
    if (this.np.noId || !idValid(id)) return;
    const r = await this.api.get<{ baseLoaded: boolean; found: boolean; voter: { fullName: string; age: number | null; ballotBox: string; clusterId: string | null } | null }>(`/api/intake/voter/${id}`);
    if (!r.baseLoaded) return;
    if (r.found && r.voter) {
      if (!this.np.fullName.trim()) this.np.fullName = r.voter.fullName;
      if (!this.np.clusterId && r.voter.clusterId) this.np.clusterId = r.voter.clusterId;
      this.npBox = r.voter.ballotBox;
      this.baseNote.set(`נמצא בבייס: ${r.voter.fullName}, קלפי ${r.voter.ballotBox}${r.voter.age ? ', גיל ' + r.voter.age : ''}`);
      this.checkNp('name');
      this.tryAddNew();
    } else {
      this.baseNote.set('לא נמצא בבייס. הנוסע יסומן "ממתין לאימות".');
    }
  }
  private npBox: string | null = null;

  protected setPassengerCluster(p: Passenger, clusterId: string | null) {
    this.passengers.update((list) => list.map((x) => (x.key === p.key ? { ...x, clusterId: clusterId ?? '' } : x)));
  }

  protected openNew() {
    this.np = { fullName: '', idNumber: '', noId: false, gender: 'Male', clusterId: null };
    this.npErr.set({});
    this.editingKey.set(null);
    this.newOpen.set(true);
    this.peopleCollapsed.set(false);
    setTimeout(() => document.getElementById('npName')?.focus());
  }

  protected editTyped(p: Passenger) {
    this.np = { fullName: p.fullName, idNumber: p.idNumber, noId: p.noId, gender: p.gender ?? 'Male', clusterId: p.clusterId || null };
    this.npErr.set({});
    this.editingKey.set(p.key);
    this.newOpen.set(true);
  }

  protected checkNp(field: 'name' | 'id' | 'cluster'): boolean {
    const e = { ...this.npErr() };
    if (field === 'name') e.name = this.np.fullName.trim() ? '' : 'צריך למלא שם';
    if (field === 'id') e.id = this.np.noId ? '' : !this.np.idNumber.trim() ? 'צריך למלא ת"ז, או לסמן "לא מסר ת"ז"' : idValid(this.np.idNumber.trim()) ? '' : 'מספר הזהות לא תקין';
    if (field === 'cluster') e.cluster = this.np.clusterId ? '' : 'צריך לבחור אשכול מהרשימה';
    this.npErr.set(e);
    return !e[field];
  }

  /** The new passenger is added as soon as every field is valid, with no "add" button (spec 5.1). */
  protected tryAddNew() {
    const np = this.np;
    const ready = np.fullName.trim() && (np.noId || idValid(np.idNumber.trim())) && np.clusterId;
    if (!ready) return;
    const key = this.editingKey() ?? 'n' + ++keySeq;
    const existing = this.passengers().find((p) => p.key === key);
    const p: Passenger = {
      key, personId: existing?.personId ?? null, fullName: np.fullName.trim(), idNumber: np.noId ? '' : np.idNumber.trim(),
      noId: np.noId, gender: np.gender, clusterId: np.clusterId!, selected: true, fromPhone: false, ballotBox: np.noId ? null : this.npBox,
    };
    this.npBox = null;
    this.baseNote.set('');
    this.passengers.update((list) => (existing ? list.map((x) => (x.key === key ? p : x)) : [...list, p]));
    this.newOpen.set(false);
    this.editingKey.set(null);
    this.peopleCollapsed.set(true);
    this.duplicate.set(null);
    setTimeout(() => document.getElementById('street')?.focus());
  }

  protected removeTyped(p: Passenger) {
    this.passengers.update((list) => list.filter((x) => x.key !== p.key));
  }

  protected notAccessible(): ClusterRef[] {
    if (!this.needsWheelchair) return [];
    const ids = new Set(this.selected().map((p) => p.clusterId));
    return this.clusters().filter((c) => ids.has(c.id) && !c.accessible);
  }

  protected family() {
    return this.selected().length > 1 && isFamily(this.selected());
  }

  protected needsText(): string[] {
    return [this.needsWheelchair && 'כיסא גלגלים', this.needsWalker && 'הליכון', this.needsHelp && 'צריך עזרה'].filter(Boolean) as string[];
  }

  /** The destination after the accessibility answer: moving replaces non-accessible clusters with the chosen one. */
  private finalCluster(p: Passenger): string {
    const moving = this.accAnswer === 'move' && this.accMoveTo && this.notAccessible().some((c) => c.id === p.clusterId);
    return moving ? this.accMoveTo! : p.clusterId;
  }

  protected destText(): string {
    // With the base, the real ballot box is read out with the place (prototype: "קלפי 34 (תלמוד תורה...)").
    const parts = [...new Set(this.selected().map((p) => {
      const name = this.cluster(this.finalCluster(p))?.name;
      return name && p.ballotBox && this.finalCluster(p) === p.clusterId ? `קלפי ${p.ballotBox} (${name})` : name;
    }).filter(Boolean))];
    return joinNames(parts as string[]);
  }

  // ---------- Step 3 ----------
  protected checkStreet() {
    this.streetErr.set(this.streetId ? '' : 'צריך לבחור רחוב מהרשימה');
    return !this.streetErr();
  }

  protected checkBuilding() {
    this.buildingErr.set(!this.building.trim() ? 'צריך למלא מספר בניין' : /^\d{1,5}$/.test(this.building.trim()) ? '' : 'ספרות בלבד');
    return !this.buildingErr();
  }

  protected streetName() {
    return this.streetItems().find((s) => s.id === this.streetId)?.label ?? '';
  }

  // ---------- Read back and save ----------
  protected missing(): string[] {
    const m: string[] = [];
    if (!this.phoneOk()) m.push('טלפון');
    const sel = this.selected();
    if (!sel.length) m.push('נוסע');
    if (sel.some((p) => !p.clusterId)) m.push('אשכול לכל נוסע');
    if (sel.some((p) => !p.gender)) m.push('גבר או אישה לכל נוסע');
    if (this.duplicate()) m.push('להוריד את מי שכבר בפנייה פתוחה');
    if (!this.streetId) m.push('רחוב מהרשימה');
    if (!/^\d{1,5}$/.test(this.building.trim())) m.push('מספר בניין');
    if (this.timeMode === 'Between' && !(this.windowFrom && this.windowTo && this.windowTo > this.windowFrom)) m.push('שעות האיסוף');
    if (this.timeMode === 'LeaveBy' && !this.urgency) m.push('סיבה לשעת היציאה');
    if (this.timeMode === 'LeaveBy' && this.urgency === 'אחר' && !this.urgencyOther.trim()) m.push('הסבר לסיבה');
    if (this.notAccessible().length && (!this.accAnswer || (this.accAnswer === 'move' && !this.accMoveTo))) m.push('תשובה על מקום לא נגיש');
    return m;
  }

  protected readback(): string {
    const sel = this.selected();
    if (!sel.length) return '';
    const esc = (s: string) => s.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]!);
    let t = `רשמתי: <b>${esc(joinNames(sel.map((p) => p.fullName)))}</b>`;
    if (this.streetId && this.building.trim())
      t += `, מ<b>${esc(this.streetName())} ${esc(this.building.trim())}${this.entrance.trim() ? ' כניסה ' + esc(this.entrance.trim()) : ''}</b>`;
    if (this.destText()) t += `, אל <b>${esc(this.destText())}</b>`;
    t += '.';
    t += this.timeMode === 'Now' ? ' נשלח נהג <b>בהקדם</b>.'
      : this.timeMode === 'Between' ? ` איסוף <b>בין ${this.windowFrom} ל-${this.windowTo}</b>.`
      : ` איסוף <b>לפני ${this.leaveBy}</b>.`;
    // The read-back mentions the return only when it is separate or there is none (spec 5.1).
    if (this.returnMode === 'CallsSeparately') t += ' לחזרה <b>תתקשרו אלינו</b> כשתסיימו.';
    if (this.returnMode === 'NoReturn') t += ' <b>הלוך בלבד</b>.';
    if (this.needsText().length) t += ` רשמתי לנהג: <b>${this.needsText().join(', ')}</b>.`;
    t += ` מספר לחזרה: <b dir="ltr" class="whitespace-nowrap">${formatPhone(this.phone)}</b>.`;
    return t;
  }

  protected async save() {
    if (this.missing().length || this.saving()) return;
    const body = {
      callerPhone: phoneDigits(this.phone),
      passengers: this.selected().map((p) => ({
        personId: p.personId, fullName: p.fullName, idNumber: p.noId ? null : p.idNumber, noId: p.noId, gender: p.gender, clusterId: this.finalCluster(p),
      })),
      streetId: this.streetId, buildingNumber: this.building.trim(), entrance: this.entrance, driverNotes: this.notes,
      communityId: this.communityId, needsWheelchair: this.needsWheelchair, needsWalker: this.needsWalker, needsHelp: this.needsHelp,
      timeMode: this.timeMode,
      windowFrom: this.timeMode === 'Between' ? this.windowFrom : null,
      windowTo: this.timeMode === 'Between' ? this.windowTo : this.timeMode === 'LeaveBy' ? this.leaveBy : null,
      urgencyReason: this.timeMode === 'LeaveBy' ? this.urgency : null,
      urgencyOther: this.urgency === 'אחר' ? this.urgencyOther : null,
      returnMode: this.returnMode,
      keepNonAccessible: this.accAnswer === 'stay',
      ignoreDuplicate: false,
    };
    this.saving.set(true);
    this.saveError.set('');
    try {
      const ed = this.editing();
      const r = ed
        ? await this.api.put<{ number: number }>(`/api/intake/requests/${ed.id}`, body)
        : await this.api.post<{ number: number }>('/api/intake/requests', body);
      this.showToast(ed ? `פנייה מס' ${r.number} עודכנה` : `נשמר: פנייה מס' ${r.number} נכנסה לתור`);
      this.reset();
      await this.loadMine();
    } catch (e) {
      const dup = e instanceof HttpErrorResponse ? e.error?.duplicate : null;
      if (dup) this.duplicate.set(dup);
      this.saveError.set(errorMessage(e));
    } finally {
      this.saving.set(false);
    }
  }

  protected reset() {
    this.phone = '';
    this.phoneErr.set('');
    this.phoneChecked.set('');
    this.openOnPhone.set([]);
    this.passengers.set([]);
    this.peopleCollapsed.set(false);
    this.newOpen.set(false);
    this.editingKey.set(null);
    this.communityId = null;
    this.needsWheelchair = this.needsWalker = this.needsHelp = false;
    this.accAnswer = this.accMoveTo = null;
    this.streetId = null;
    this.building = this.entrance = this.notes = '';
    this.streetErr.set('');
    this.buildingErr.set('');
    this.timeMode = 'Now';
    this.urgency = this.urgencyOther = '';
    this.returnMode = 'DriverWaits';
    this.saveError.set('');
    this.duplicate.set(null);
    this.editing.set(null);
    setTimeout(() => document.getElementById('phone')?.focus());
  }

  private showToast(text: string) {
    this.toast.set(text);
    setTimeout(() => this.toast() === text && this.toast.set(''), 4000);
  }

  // ---------- Edit an existing request ----------
  protected async openForEdit(id: string) {
    const r = await this.api.get<{
      id: string; number: number; callerPhone: string; status: Status; streetId: string; buildingNumber: string;
      entrance: string | null; driverNotes: string | null; communityId: string | null;
      needsWheelchair: boolean; needsWalker: boolean; needsHelp: boolean;
      timeMode: TimeMode; windowFrom: string | null; windowTo: string | null; urgencyReason: string | null; returnMode: ReturnMode;
      passengers: { personId: string; fullName: string; idNumber: string | null; noId: boolean; gender: Gender; clusterId: string }[];
    }>(`/api/intake/requests/${id}`);
    this.reset();
    this.hits.set(null);
    this.query = '';
    this.editing.set({ id: r.id, number: r.number });
    this.phone = formatPhone(r.callerPhone);
    this.phoneChecked.set(r.callerPhone);
    this.passengers.set(r.passengers.map((p) => ({
      key: 'e' + ++keySeq, personId: p.personId, fullName: p.fullName, idNumber: p.idNumber ?? '', noId: p.noId,
      gender: p.gender, clusterId: p.clusterId, selected: true, fromPhone: false,
    })));
    this.peopleCollapsed.set(true);
    this.streetId = r.streetId;
    this.building = r.buildingNumber;
    this.entrance = r.entrance ?? '';
    this.notes = r.driverNotes ?? '';
    this.communityId = r.communityId;
    this.needsWheelchair = r.needsWheelchair;
    this.needsWalker = r.needsWalker;
    this.needsHelp = r.needsHelp;
    if (r.needsWheelchair && this.notAccessible().length) this.accAnswer = 'stay';
    this.timeMode = r.timeMode;
    if (r.timeMode === 'Between') {
      this.windowFrom = r.windowFrom?.slice(0, 5) ?? '';
      this.windowTo = r.windowTo?.slice(0, 5) ?? '';
    }
    if (r.timeMode === 'LeaveBy') {
      this.leaveBy = r.windowTo?.slice(0, 5) ?? '';
      const reason = r.urgencyReason ?? '';
      this.urgency = reason.startsWith('אחר: ') ? 'אחר' : reason;
      this.urgencyOther = reason.startsWith('אחר: ') ? reason.slice(5) : '';
    }
    this.returnMode = r.returnMode;
  }

  // ---------- Search ----------
  protected async search() {
    const q = this.query.trim();
    const seq = ++this.searchSeq;
    if (q.length < 2) {
      this.hits.set(null);
      return;
    }
    const r = await this.api.get<SearchHit[]>(`/api/intake/search?q=${encodeURIComponent(q)}`);
    if (seq === this.searchSeq) this.hits.set(r);
  }

  protected hitNames(h: SearchHit) {
    return joinNames(h.passengers.map((p) => p.fullName));
  }

  protected openCancel(h: SearchHit) {
    this.cancelOf.set(h);
    this.cancelReason = this.cancelOther = '';
    this.cancelErr.set('');
  }

  protected async confirmCancel() {
    const h = this.cancelOf();
    if (!h) return;
    if (!this.cancelReason) return this.cancelErr.set('צריך לבחור סיבה');
    if (this.cancelReason === 'אחר' && !this.cancelOther.trim()) return this.cancelErr.set('צריך לכתוב בקצרה למה');
    try {
      await this.api.post(`/api/intake/requests/${h.id}/cancel`, { reason: this.cancelReason, other: this.cancelOther });
      this.cancelOf.set(null);
      this.showToast(`פנייה מס' ${h.number} בוטלה`);
      await this.search();
      await this.loadMine();
    } catch (e) {
      this.cancelErr.set(errorMessage(e));
    }
  }

  protected async logout() {
    await this.auth.logout();
    await this.router.navigateByUrl('/login');
  }
}
