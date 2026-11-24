import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../core/api';
import { errorMessage } from '../core/auth.service';
import { Dialog } from '../shared/dialog';
import { Icon } from '../shared/icon';
import { markHtml } from '../shared/mark';

interface Row {
  id: string;
  name: string;
  source?: 'Official' | 'Manual' | 'Catalog';
}

interface CatalogItem {
  id: number;
  name: string;
  chosen: boolean;
}

/**
 * Shared record list for communities and streets (spec 3.1): search, add, inline edit
 * (Enter saves, Esc cancels), remove, a running number, and "showing X of Y".
 * Communities also pick from the system's shared list; those keep their name.
 */
@Component({
  selector: 'app-list-manager',
  imports: [FormsModule, Dialog, Icon],
  template: `
    <section class="card overflow-hidden">
      <div class="flex flex-wrap items-center gap-3 border-b border-line px-5 py-4">
        <h1 class="text-xl font-semibold">{{ title() }}</h1>
        @if (kind() === 'communities') {
          <button type="button" class="btn-primary ms-auto h-10 px-4" (click)="openCatalog()"><app-icon name="check" [size]="18" /> בחירה מהרשימה הכללית</button>
        }
        @if (importable()) {
          <button type="button" class="btn-secondary ms-auto h-10 px-4" [disabled]="busy()" (click)="reimport()">
            <app-icon name="refresh" [size]="18" /> ייבא מחדש מהמאגר הרשמי
          </button>
        }
      </div>

      <div class="grid gap-3 border-b border-line p-5 md:grid-cols-2">
        <div class="field">
          <label class="label" [for]="kind() + '-search'">מה לחפש?</label>
          <div class="relative">
            <span class="pointer-events-none absolute inset-y-0 start-3 grid place-items-center text-muted"><app-icon name="search" [size]="18" /></span>
            <input [id]="kind() + '-search'" class="input ps-10" [ngModel]="query()" (ngModelChange)="query.set($event)" [placeholder]="'חיפוש ' + itemWord()" />
          </div>
        </div>
        <form class="field" (ngSubmit)="add()">
          <label class="label" [for]="kind() + '-new'">{{ addQuestion() }}</label>
          <div class="flex gap-2">
            <input [id]="kind() + '-new'" name="newName" class="input" [class.invalid]="addError()" [(ngModel)]="newName" (input)="addError.set('')" />
            <button class="btn-primary shrink-0" type="submit" [disabled]="busy()"><app-icon name="plus" [size]="18" /> הוספה</button>
          </div>
          <div class="error-text">{{ addError() }}</div>
        </form>
      </div>

      @if (notice()) {
        <p class="notice mx-5 mt-4" role="status">{{ notice() }}</p>
      }
      <p class="px-5 pt-4 text-sm text-muted">מוצגים {{ shown().length }} מתוך {{ rows().length }}</p>

      <ol class="p-3 sm:p-5">
        <li class="hidden grid-cols-[48px_minmax(0,1fr)_140px_200px] gap-3 px-3 pb-2 text-sm font-medium text-muted md:grid">
          <span>מס'</span><span>שם</span><span>מקור</span><span></span>
        </li>
        @for (r of shown(); track r.id; let i = $index) {
          <li class="grid grid-cols-[40px_minmax(0,1fr)] items-center gap-x-3 gap-y-2 rounded-xl px-3 py-2 odd:bg-bg md:grid-cols-[48px_minmax(0,1fr)_140px_200px]">
            <span class="text-sm tabular-nums text-muted">{{ i + 1 }}</span>
            @if (editingId() === r.id) {
              <input class="input h-10" [attr.aria-label]="'שם חדש ל' + r.name" [(ngModel)]="editName" (input)="editError.set('')"
                [class.invalid]="editError()" (keydown.enter)="saveEdit(r)" (keydown.escape)="editingId.set(null)" #ed />
            } @else {
              <span class="min-w-0 truncate font-medium" [innerHTML]="mark(r.name)"></span>
            }
            <span class="col-start-2 text-sm md:col-start-auto">
              @if (r.source === 'Manual') { <span class="rounded-full bg-action-soft px-2.5 py-0.5 text-ink">נוסף ידנית</span> }
              @if (r.source === 'Official') { <span class="text-muted">מאגר רשמי</span> }
              @if (r.source === 'Catalog') { <span class="text-muted">מהרשימה הכללית</span> }
            </span>
            <span class="col-start-2 flex gap-2 md:col-start-auto md:justify-end">
              @if (editingId() === r.id) {
                <button type="button" class="btn-primary h-10 px-4" (click)="saveEdit(r)">שמור</button>
                <button type="button" class="btn-secondary h-10 px-4" (click)="editingId.set(null)">ביטול</button>
              } @else {
                @if (r.source !== 'Catalog') {
                  <button type="button" class="btn-secondary h-10 px-3" (click)="startEdit(r)"><app-icon name="edit" [size]="18" /> ערוך</button>
                }
                <button type="button" class="btn-secondary h-10 px-3 text-bad" (click)="toRemove.set(r)"><app-icon name="trash" [size]="18" /> הסר</button>
              }
            </span>
            @if (editingId() === r.id && editError()) {
              <span class="error-text col-start-2 md:col-span-3">{{ editError() }}</span>
            }
          </li>
        } @empty {
          <li class="px-3 py-6 text-center text-muted">{{ rows().length ? 'לא נמצא ' + itemWord() + ' שמתאים לחיפוש' : emptyText() }}</li>
        }
      </ol>
    </section>

    <app-dialog [open]="catalogOpen()" title="אילו קהילות יופיעו בעיר?" [width]="720" (closed)="catalogOpen.set(false)">
      <div class="grid gap-3">
        <div class="relative">
          <span class="pointer-events-none absolute inset-y-0 start-3 grid place-items-center text-muted"><app-icon name="search" [size]="18" /></span>
          <input class="input ps-10" placeholder="חיפוש ברשימה" aria-label="חיפוש ברשימה הכללית" [ngModel]="catalogQuery()" (ngModelChange)="catalogQuery.set($event)" />
        </div>
        <div class="flex flex-wrap items-center gap-3 text-sm">
          <span class="text-muted">נבחרו {{ picked().size }} מתוך {{ catalog().length }}</span>
          @if (catalogQuery().trim()) { <span class="text-muted">· מוצגות {{ catalogShown().length }}</span> }
        </div>
        <div class="grid max-h-[50vh] gap-x-4 gap-y-1 overflow-y-auto rounded-2xl border-[1.5px] border-line p-3 sm:grid-cols-2 md:grid-cols-3">
          @for (c of catalogShown(); track c.id) {
            <label class="flex min-h-10 cursor-pointer items-center gap-2 rounded-lg px-2 hover:bg-soft">
              <input type="checkbox" class="size-4 shrink-0 accent-[var(--color-action)]" [checked]="picked().has(c.id)" (change)="togglePick(c.id)" />
              <span [innerHTML]="markCatalog(c.name)"></span>
            </label>
          } @empty { <p class="text-muted">לא נמצאה קהילה שמתאימה לחיפוש</p> }
        </div>
        <p class="text-sm text-muted">רק הקהילות שמסומנות מופיעות למוקדן. הורדת סימון מוציאה את הקהילה מרשימת העיר. קהילה שאין ברשימה אפשר להוסיף ידנית.</p>
        @if (catalogError()) { <p class="notice bad" role="alert">{{ catalogError() }}</p> }
        <div class="flex gap-2">
          <button type="button" class="btn-primary" [disabled]="busy()" (click)="saveCatalog()">שמירה</button>
          <button type="button" class="btn-secondary" (click)="catalogOpen.set(false)">ביטול</button>
        </div>
      </div>
    </app-dialog>

    <app-dialog [open]="!!toRemove()" title="הסרה מהרשימה" [width]="440" (closed)="toRemove.set(null)">
      <p>להסיר את "{{ toRemove()?.name }}" מהרשימה?</p>
      @if (removeError()) { <p class="notice bad mt-3">{{ removeError() }}</p> }
      <div class="mt-5 flex gap-2">
        <button type="button" class="btn-primary !bg-bad" (click)="remove()">הסר</button>
        <button type="button" class="btn-secondary" (click)="toRemove.set(null)">ביטול</button>
      </div>
    </app-dialog>
  `,
})
export class ListManager implements OnInit {
  private readonly api = inject(Api);
  readonly kind = input.required<'communities' | 'streets'>();
  readonly title = input.required<string>();
  readonly itemWord = input.required<string>();
  readonly addQuestion = input.required<string>();
  readonly emptyText = input.required<string>();
  readonly importable = input(false);

  protected readonly rows = signal<Row[]>([]);
  protected readonly busy = signal(false);
  protected readonly notice = signal('');
  protected readonly addError = signal('');
  protected readonly editError = signal('');
  protected readonly removeError = signal('');
  protected readonly editingId = signal<string | null>(null);
  protected readonly toRemove = signal<Row | null>(null);
  protected readonly query = signal('');
  protected newName = '';
  protected editName = '';

  protected readonly catalogOpen = signal(false);
  protected readonly catalog = signal<CatalogItem[]>([]);
  protected readonly picked = signal(new Set<number>());
  protected readonly catalogQuery = signal('');
  protected readonly catalogError = signal('');
  protected readonly catalogShown = computed(() => {
    const q = this.catalogQuery().trim();
    return q ? this.catalog().filter((c) => c.name.includes(q)) : this.catalog();
  });

  protected async openCatalog() {
    const list = await this.api.get<CatalogItem[]>('/api/city/communities/catalog');
    this.catalog.set(list);
    this.picked.set(new Set(list.filter((c) => c.chosen).map((c) => c.id)));
    this.catalogQuery.set('');
    this.catalogError.set('');
    this.catalogOpen.set(true);
  }

  protected togglePick(id: number) {
    const next = new Set(this.picked());
    next.has(id) ? next.delete(id) : next.add(id);
    this.picked.set(next);
  }

  protected markCatalog(name: string) {
    return markHtml(name, this.catalogQuery());
  }

  protected async saveCatalog() {
    this.busy.set(true);
    try {
      await this.api.put('/api/city/communities/catalog', { ids: [...this.picked()] });
      this.catalogOpen.set(false);
      this.notice.set(`נשמר: ${this.picked().size} קהילות מהרשימה הכללית`);
      await this.load();
    } catch (e) {
      this.catalogError.set(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  protected readonly shown = computed(() => {
    const q = this.query().trim();
    return q ? this.rows().filter((r) => r.name.includes(q)) : this.rows();
  });

  private get url() {
    return `/api/city/${this.kind()}`;
  }

  async ngOnInit() {
    await this.load();
  }

  private async load() {
    this.rows.set(await this.api.get<Row[]>(this.url));
  }

  protected async add() {
    if (!this.newName.trim()) {
      this.addError.set('צריך למלא שם');
      return;
    }
    this.busy.set(true);
    try {
      await this.api.post(this.url, { name: this.newName });
      this.newName = '';
      await this.load();
    } catch (e) {
      this.addError.set(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  protected startEdit(r: Row) {
    this.editingId.set(r.id);
    this.editName = r.name;
    this.editError.set('');
    setTimeout(() => (document.querySelector('li input.input') as HTMLInputElement | null)?.focus());
  }

  protected async saveEdit(r: Row) {
    if (!this.editName.trim()) {
      this.editError.set('צריך למלא שם');
      return;
    }
    try {
      await this.api.put(`${this.url}/${r.id}`, { name: this.editName });
      this.editingId.set(null);
      await this.load();
    } catch (e) {
      this.editError.set(errorMessage(e));
    }
  }

  protected async remove() {
    const r = this.toRemove();
    if (!r) return;
    this.removeError.set('');
    try {
      await this.api.delete(`${this.url}/${r.id}`);
      this.toRemove.set(null);
      await this.load();
    } catch (e) {
      this.removeError.set(errorMessage(e));
    }
  }

  protected async reimport() {
    this.busy.set(true);
    this.notice.set('מייבא מהמאגר הרשמי...');
    try {
      const r = await this.api.post<{ added: number; renamed: number; removed: number; total: number }>(`${this.url}/import`);
      this.notice.set(`הייבוא הסתיים: נוספו ${r.added}, שמות שהשתנו ${r.renamed}, הוסרו ${r.removed}. ברשימה ${r.total} רחובות.`);
      await this.load();
    } catch (e) {
      this.notice.set(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  protected mark(name: string): string {
    return markHtml(name, this.query());
  }

}

@Component({
  selector: 'app-communities',
  imports: [ListManager],
  template: `<app-list-manager kind="communities" title="קהילות" itemWord="קהילה" addQuestion="איזו קהילה להוסיף?"
    emptyText='עוד אין קהילות. "בחירה מהרשימה הכללית" מסמנת את הקהילות של העיר, והן מופיעות למוקדן כשהוא קולט פנייה.' />`,
})
export class CommunitiesPage {}

@Component({
  selector: 'app-streets',
  imports: [ListManager],
  template: `<app-list-manager kind="streets" title="רחובות" itemWord="רחוב" addQuestion="איזה רחוב להוסיף ידנית?" [importable]="true"
    emptyText="עוד אין רחובות. אפשר לייבא את רשימת הרחובות של העיר מהמאגר הרשמי." />`,
})
export class StreetsPage {}
