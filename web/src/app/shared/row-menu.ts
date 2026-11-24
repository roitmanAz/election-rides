import { Component, ElementRef, inject, input, signal, viewChild } from '@angular/core';
import { Icon } from './icon';

/**
 * "⋮" button at the end of a row that opens a short list of actions.
 * Usage: <app-row-menu label="פעולות לירושלים"><button (click)="...">ניהול</button></app-row-menu>
 * The list floats above the whole page (fixed position next to the button), so a card around the row
 * never cuts it; it opens upward when there is no room below. Closes after a choice, on a click
 * outside, on Esc, and when the page scrolls or resizes.
 */
@Component({
  selector: 'app-row-menu',
  imports: [Icon],
  host: {
    class: 'inline-block',
    '(document:click)': 'outside($event)',
    '(document:keydown.escape)': 'open.set(false)',
    '(window:scroll)': 'open.set(false)',
    '(window:resize)': 'open.set(false)',
  },
  template: `
    <button #btn type="button" class="grid size-10 place-items-center rounded-xl text-ink hover:bg-soft2" [class.bg-soft2]="open()"
      [attr.aria-label]="label()" aria-haspopup="menu" [attr.aria-expanded]="open()" (click)="toggle()">
      <app-icon name="dots" [size]="20" />
    </button>
    @if (open()) {
      <div role="menu" class="fixed z-[60] grid min-w-44 gap-0.5 rounded-2xl bg-surface p-1.5 shadow-[var(--shadow-pop)] ring-1 ring-line
        [&>button]:h-10 [&>button]:rounded-xl [&>button]:px-3 [&>button]:text-start [&>button]:font-medium [&>button:hover]:bg-soft"
        [style.left.px]="pos().left" [style.top.px]="pos().top" [style.bottom.px]="pos().bottom"
        (click)="open.set(false)">
        <ng-content />
      </div>
    }
  `,
})
export class RowMenu {
  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly btn = viewChild.required<ElementRef<HTMLButtonElement>>('btn');
  readonly label = input('פעולות');
  protected readonly open = signal(false);
  protected readonly pos = signal<{ left: number; top: number | null; bottom: number | null }>({ left: 0, top: 0, bottom: null });

  protected toggle() {
    if (this.open()) return this.open.set(false);
    const r = this.btn().nativeElement.getBoundingClientRect();
    // The list starts at the button's left edge (the row's end in right-to-left) and grows to the right.
    const roomBelow = window.innerHeight - r.bottom;
    this.pos.set(roomBelow > 160
      ? { left: r.left, top: r.bottom + 4, bottom: null }
      : { left: r.left, top: null, bottom: window.innerHeight - r.top + 4 });
    this.open.set(true);
  }

  protected outside(e: Event) {
    if (this.open() && !this.host.nativeElement.contains(e.target as Node)) this.open.set(false);
  }
}
