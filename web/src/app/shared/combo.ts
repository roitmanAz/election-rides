import { Component, computed, input, model, output, signal } from '@angular/core';
import { markHtml } from './mark';

export interface ComboItem {
  id: string;
  label: string;
  sub?: string;
}

/**
 * Pick-from-list field (spec, intake rules): substring filter, the matched text is marked,
 * and only an item from the list can be chosen, never free text. Nothing opens until something is typed.
 */
@Component({
  selector: 'app-combo',
  template: `
    <div class="relative">
      <input [id]="inputId()" class="input" autocomplete="off" role="combobox" [attr.aria-expanded]="openList()"
        [class.invalid]="invalid()" [placeholder]="placeholder()" [value]="text()"
        (input)="onType($any($event.target).value)" (blur)="onBlur()"
        (keydown.arrowdown)="move(1, $event)" (keydown.arrowup)="move(-1, $event)" (keydown.enter)="pickActive($event)"
        (keydown.escape)="openList.set(false)" />
      @if (showList()) {
        <ul class="absolute inset-x-0 top-full z-50 mt-1 max-h-64 overflow-y-auto rounded-2xl bg-surface p-1.5 shadow-[var(--shadow-pop)]" role="listbox">
          @for (it of filtered(); track it.id; let i = $index) {
            <li role="option" [attr.aria-selected]="i === active()"
              class="cursor-pointer rounded-xl px-3 py-2" [class.bg-soft2]="i === active()"
              (mousedown)="$event.preventDefault(); pick(it)" (mouseenter)="active.set(i)">
              <span [innerHTML]="mark(it.label)"></span>
              @if (it.sub) { <small class="ms-2 text-muted">{{ it.sub }}</small> }
            </li>
          } @empty {
            <li class="px-3 py-2 text-muted">{{ emptyText() }}</li>
          }
        </ul>
      }
    </div>
  `,
})
export class Combo {
  readonly items = input.required<ComboItem[]>();
  readonly inputId = input('combo');
  readonly placeholder = input('');
  readonly emptyText = input('לא נמצא ברשימה');
  readonly invalid = input(false);
  /** How many letters before the list opens (e.g. 2 for a search on the server). */
  readonly minChars = input(1);
  /** The chosen item id, or null. */
  readonly value = model<string | null>(null);
  readonly typed = output<string>();
  readonly left = output<void>();

  protected readonly query = signal<string | null>(null);
  protected readonly openList = signal(false);
  protected readonly active = signal(0);

  protected readonly showList = computed(() => this.openList() && (this.query() ?? '').trim().length >= this.minChars());
  protected readonly text = computed(() => this.query() ?? this.items().find((i) => i.id === this.value())?.label ?? '');
  protected readonly filtered = computed(() => {
    const q = (this.query() ?? '').trim();
    const list = this.items();
    return (q ? list.filter((i) => i.label.includes(q)) : list).slice(0, 60);
  });

  protected onType(v: string) {
    this.query.set(v);
    this.value.set(null);
    this.active.set(0);
    this.openList.set(true);
    this.typed.emit(v);
  }

  protected move(step: number, e: Event) {
    e.preventDefault();
    const n = this.filtered().length;
    if (n) this.active.set((this.active() + step + n) % n);
  }

  protected pickActive(e: Event) {
    const it = this.filtered()[this.active()];
    if (this.showList() && it) {
      e.preventDefault();
      this.pick(it);
    }
  }

  protected pick(it: ComboItem) {
    this.value.set(it.id);
    this.query.set(null);
    this.openList.set(false);
  }

  protected onBlur() {
    this.openList.set(false);
    // Free text is not kept: only a picked item counts.
    if (this.value() === null) this.query.set(null);
    this.left.emit();
  }

  protected mark(label: string): string {
    return markHtml(label, this.query());
  }

}
