import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Digits } from '../shared/digits';
import { Api } from '../core/api';
import { errorMessage } from '../core/auth.service';
import { Combo, ComboItem } from '../shared/combo';
import { Dialog } from '../shared/dialog';
import { Icon } from '../shared/icon';

interface Cluster {
  id: string;
  number: number | null;
  name: string;
  shortName: string;
  accessible: boolean;
  dropoffNote: string | null;
  buildingNumber: string;
  streetId: string;
  street: string;
  boxes: string[];
}

interface Form {
  name: string;
  shortName: string;
  number: string;
  boxes: string;
  streetId: string | null;
  buildingNumber: string;
  accessible: boolean | null;
  dropoffNote: string;
}

const EMPTY: Form = { name: '', shortName: '', number: '', boxes: '', streetId: null, buildingNumber: '', accessible: null, dropoffNote: '' };

/** Shows "31–35" for a run of numbers, as the city admin typed it. */
export function boxesText(boxes: string[]): string {
  const out: string[] = [];
  let i = 0;
  while (i < boxes.length) {
    let j = i;
    while (j + 1 < boxes.length && /^\d+$/.test(boxes[j]) && +boxes[j + 1] === +boxes[j] + 1) j++;
    out.push(j > i ? `${boxes[i]}–${boxes[j]}` : boxes[i]);
    i = j + 1;
  }
  return out.join(', ');
}

/** Clusters (spec 3.1): table with plate, name, address, boxes, accessibility and drop-off note; filter; edit dialog. */
@Component({
  selector: 'app-clusters',
  imports: [FormsModule, Dialog, Icon, Combo, Digits],
  template: `
    <section class="card overflow-hidden">
      <div class="flex flex-wrap items-center gap-3 border-b border-line px-5 py-4">
        <h1 class="text-xl font-semibold">אשכולות</h1>
        <div class="flex gap-1 rounded-full bg-soft p-1" role="group" aria-label="סינון">
          @for (f of filters; track f.key) {
            <button type="button" class="h-9 rounded-full px-4 text-sm font-medium"
              [class]="filter() === f.key ? 'bg-ink text-white' : 'text-muted'" (click)="filter.set(f.key)">{{ f.label }}</button>
          }
        </div>
        <button type="button" class="btn-primary ms-auto" (click)="openNew()"><app-icon name="plus" [size]="18" /> אשכול חדש</button>
      </div>

      <div class="p-3 sm:p-5">
        <div class="hidden grid-cols-[64px_minmax(0,1.4fr)_minmax(0,1fr)_110px_90px_minmax(0,1.2fr)_96px] gap-3 px-3 pb-2 text-sm font-medium text-muted lg:grid">
          <span>לוחית</span><span>שם</span><span>כתובת</span><span>קלפיות</span><span>נגישות</span><span>נקודת הורדה והערות</span><span></span>
        </div>
        @for (c of shown(); track c.id) {
          <div class="grid grid-cols-[64px_minmax(0,1fr)] items-center gap-x-3 gap-y-1 rounded-xl px-3 py-2.5 odd:bg-bg
            lg:grid-cols-[64px_minmax(0,1.4fr)_minmax(0,1fr)_110px_90px_minmax(0,1.2fr)_96px]">
            <span class="row-span-5 self-start lg:row-span-1 lg:self-center">
              <span class="inline-flex h-7 min-w-9 items-center justify-center rounded-lg bg-ink px-2 text-sm font-semibold text-white"
                [title]="c.name + ', ' + c.street + ' ' + c.buildingNumber">{{ c.number ?? c.shortName }}</span>
            </span>
            <span class="min-w-0 font-medium">{{ c.name }}</span>
            <span class="min-w-0 text-muted lg:text-ink"><span class="lg:hidden">כתובת: </span>{{ c.street }} {{ c.buildingNumber }}</span>
            <span class="tabular-nums"><span class="text-muted lg:hidden">קלפיות: </span>{{ boxesText(c.boxes) }}</span>
            <span>
              @if (c.accessible) {
                <span class="inline-flex items-center gap-1 rounded-full bg-soft px-2.5 py-0.5 text-sm" title="נגיש לכיסא גלגלים"><app-icon name="wc" [size]="16" /> נגיש</span>
              } @else {
                <span class="text-sm text-muted">לא נגיש</span>
              }
            </span>
            <span class="min-w-0 text-sm text-muted">{{ c.dropoffNote }}</span>
            <span class="col-start-2 flex gap-2 lg:col-start-auto lg:justify-end">
              <button type="button" class="btn-secondary h-10 px-3" (click)="openEdit(c)"><app-icon name="edit" [size]="18" /> ערוך</button>
            </span>
          </div>
        } @empty {
          <p class="px-3 py-6 text-center text-muted">{{ list().length ? 'אין אשכולות שמתאימים לסינון' : 'עוד אין אשכולות. אשכול הוא בניין עם כמה קלפיות, ואליו הנהג מסיע.' }}</p>
        }
      </div>
    </section>

    <app-dialog [open]="dialogOpen()" [title]="editId() ? 'עריכת אשכול' : 'אשכול חדש'" [width]="620" (closed)="dialogOpen.set(false)">
      <form class="grid gap-3 sm:grid-cols-2" (ngSubmit)="save()" novalidate>
        <div class="field sm:col-span-2">
          <label class="label" for="cl-name">מה שם המקום?<span class="req">*</span></label>
          <input id="cl-name" name="name" class="input" [(ngModel)]="form.name" [class.invalid]="err().name" (blur)="check('name')" />
          <div class="error-text">{{ err().name }}</div>
        </div>
        <div class="field">
          <label class="label" for="cl-short">מה השם הקצר ללוחית?<span class="req">*</span></label>
          <input id="cl-short" name="shortName" class="input" maxlength="14" [(ngModel)]="form.shortName" [class.invalid]="err().shortName" (blur)="check('shortName')" />
          <div class="error-text">{{ err().shortName }}</div>
        </div>
        <div class="field">
          <label class="label" for="cl-num">מה מספר האשכול?</label>
          <input id="cl-num" name="number" class="input" appDigits="4" [(ngModel)]="form.number" [class.invalid]="err().number" (blur)="check('number')" />
          <div class="error-text">{{ err().number }}</div>
        </div>
        <div class="field sm:col-span-2">
          <label class="label" for="cl-boxes">אילו קלפיות יש בו?<span class="req">*</span></label>
          <input id="cl-boxes" name="boxes" class="input" placeholder="למשל 31-35 או 10, 12, 14-16" [(ngModel)]="form.boxes" [class.invalid]="err().boxes" (blur)="check('boxes')" />
          <div class="error-text">{{ err().boxes }}</div>
        </div>
        <div class="field">
          <label class="label" for="cl-street">באיזה רחוב?<span class="req">*</span></label>
          <app-combo inputId="cl-street" [items]="streets()" [(value)]="form.streetId" [invalid]="!!err().streetId"
            emptyText="הרחוב לא ברשימה. אפשר להוסיף אותו במסך הרחובות." (left)="check('streetId')" />
          <div class="error-text">{{ err().streetId }}</div>
        </div>
        <div class="field">
          <label class="label" for="cl-bld">מה מספר הבניין?<span class="req">*</span></label>
          <input id="cl-bld" name="buildingNumber" class="input" maxlength="10" [(ngModel)]="form.buildingNumber" [class.invalid]="err().buildingNumber" (blur)="check('buildingNumber')" />
          <div class="error-text">{{ err().buildingNumber }}</div>
        </div>
        <fieldset class="field sm:col-span-2">
          <legend class="label mb-1.5">המקום נגיש לכיסא גלגלים?<span class="req">*</span></legend>
          <div class="flex w-max gap-1 rounded-xl bg-soft p-1" [class.ring-2]="err().accessible" [class.ring-bad]="err().accessible">
            <button type="button" class="h-10 rounded-[10px] px-5 font-medium" [class]="form.accessible === true ? 'bg-ink text-white' : ''" (click)="form.accessible = true; check('accessible')">כן</button>
            <button type="button" class="h-10 rounded-[10px] px-5 font-medium" [class]="form.accessible === false ? 'bg-ink text-white' : ''" (click)="form.accessible = false; check('accessible')">לא</button>
          </div>
          <div class="error-text">{{ err().accessible }}</div>
        </fieldset>
        <div class="field sm:col-span-2">
          <label class="label" for="cl-drop">איפה להוריד, ומה עוד הנהג צריך לדעת?</label>
          <textarea id="cl-drop" name="dropoffNote" rows="2" maxlength="300" class="input h-auto py-2.5" [(ngModel)]="form.dropoffNote"></textarea>
        </div>
        @if (saveError()) { <p class="notice bad sm:col-span-2" role="alert">{{ saveError() }}</p> }
        <div class="flex flex-wrap gap-2 sm:col-span-2">
          <button class="btn-primary" type="submit" [disabled]="busy()">שמירה</button>
          <button class="btn-secondary" type="button" (click)="dialogOpen.set(false)">ביטול</button>
          @if (editId()) {
            <button class="btn-secondary ms-auto text-bad" type="button" (click)="confirmRemove.set(true)"><app-icon name="trash" [size]="18" /> הסרת האשכול</button>
          }
        </div>
      </form>
    </app-dialog>

    <app-dialog [open]="confirmRemove()" title="הסרת אשכול" [width]="440" (closed)="confirmRemove.set(false)">
      <p>להסיר את האשכול "{{ form.name }}" ואת הקלפיות שלו?</p>
      <div class="mt-5 flex gap-2">
        <button type="button" class="btn-primary !bg-bad" (click)="remove()">הסר</button>
        <button type="button" class="btn-secondary" (click)="confirmRemove.set(false)">ביטול</button>
      </div>
    </app-dialog>
  `,
})
export class ClustersPage implements OnInit {
  private readonly api = inject(Api);
  protected readonly boxesText = boxesText;
  protected readonly filters = [
    { key: 'all', label: 'הכול' },
    { key: 'yes', label: 'נגישים' },
    { key: 'no', label: 'לא נגישים' },
  ] as const;

  protected readonly list = signal<Cluster[]>([]);
  protected readonly streets = signal<ComboItem[]>([]);
  protected readonly filter = signal<'all' | 'yes' | 'no'>('all');
  protected readonly shown = computed(() =>
    this.list().filter((c) => this.filter() === 'all' || c.accessible === (this.filter() === 'yes')),
  );

  protected readonly dialogOpen = signal(false);
  protected readonly confirmRemove = signal(false);
  protected readonly editId = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly saveError = signal('');
  protected readonly err = signal<Partial<Record<keyof Form, string>>>({});
  protected form: Form = { ...EMPTY };

  async ngOnInit() {
    await this.load();
  }

  private async load() {
    const [clusters, streets] = await Promise.all([
      this.api.get<Cluster[]>('/api/city/clusters'),
      this.api.get<{ id: string; name: string }[]>('/api/city/streets'),
    ]);
    this.list.set(clusters);
    this.streets.set(streets.map((s) => ({ id: s.id, label: s.name })));
  }

  protected openNew() {
    this.form = { ...EMPTY };
    this.editId.set(null);
    this.openDialog();
  }

  protected openEdit(c: Cluster) {
    this.form = {
      name: c.name, shortName: c.shortName, number: c.number?.toString() ?? '', boxes: boxesText(c.boxes),
      streetId: c.streetId, buildingNumber: c.buildingNumber, accessible: c.accessible, dropoffNote: c.dropoffNote ?? '',
    };
    this.editId.set(c.id);
    this.openDialog();
  }

  private openDialog() {
    this.err.set({});
    this.saveError.set('');
    this.dialogOpen.set(true);
  }

  protected check(field: keyof Form): boolean {
    const f = this.form;
    const msg: Record<string, () => string> = {
      name: () => (f.name.trim() ? '' : 'צריך למלא את שם המקום'),
      shortName: () => (f.shortName.trim() ? '' : 'צריך למלא שם קצר ללוחית'),
      number: () => (!f.number.trim() || /^\d{1,4}$/.test(f.number.trim()) ? '' : 'מספר האשכול הוא מספר בלבד'),
      boxes: () => (/^\s*[\d.,\s\-–]+\s*$/.test(f.boxes) && /\d/.test(f.boxes) ? '' : 'אפשר לכתוב מספר, טווח (31-35) או רשימה (10, 12, 14-16)'),
      streetId: () => (f.streetId ? '' : 'צריך לבחור רחוב מהרשימה'),
      buildingNumber: () => (f.buildingNumber.trim() ? '' : 'צריך למלא מספר בניין'),
      accessible: () => (f.accessible === null ? 'צריך לבחור כן או לא' : ''),
    };
    const m = msg[field]?.() ?? '';
    this.err.update((e) => ({ ...e, [field]: m }));
    return !m;
  }

  protected async save() {
    const fields: (keyof Form)[] = ['name', 'shortName', 'number', 'boxes', 'streetId', 'buildingNumber', 'accessible'];
    if (!fields.map((k) => this.check(k)).every(Boolean)) return;
    const f = this.form;
    const body = {
      name: f.name, shortName: f.shortName, number: f.number.trim() ? +f.number : null, boxes: f.boxes,
      streetId: f.streetId, buildingNumber: f.buildingNumber, accessible: f.accessible, dropoffNote: f.dropoffNote,
    };
    this.busy.set(true);
    this.saveError.set('');
    try {
      const id = this.editId();
      if (id) await this.api.put(`/api/city/clusters/${id}`, body);
      else await this.api.post('/api/city/clusters', body);
      this.dialogOpen.set(false);
      await this.load();
    } catch (e) {
      this.saveError.set(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  protected async remove() {
    const id = this.editId();
    if (!id) return;
    await this.api.delete(`/api/city/clusters/${id}`);
    this.confirmRemove.set(false);
    this.dialogOpen.set(false);
    await this.load();
  }
}
