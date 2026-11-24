import { Component, ElementRef, inject, input, signal } from '@angular/core';
import { Icon } from './icon';

/**
 * "⋮" button at the end of a row that opens a short list of actions.
 * Usage: <app-row-menu label="פעולות לירושלים"><button menuItem (click)="...">ניהול</button></app-row-menu>
 * Closes after a choice, on a click outside, and on Esc.
 */
@Component({
  selector: 'app-row-menu',
  imports: [Icon],
  host: { class: 'relative inline-block', '(document:click)': 'outside($event)', '(document:keydown.escape)': 'open.set(false)' },
  template: `
    <button type="button" class="grid size-10 place-items-center rounded-xl text-ink hover:bg-soft2" [class.bg-soft2]="open()"
      [attr.aria-label]="label()" aria-haspopup="menu" [attr.aria-expanded]="open()" (click)="open.set(!open())">
      <app-icon name="dots" [size]="20" />
    </button>
    @if (open()) {
      <div role="menu" class="absolute end-0 top-full z-40 mt-1 grid min-w-44 gap-0.5 rounded-2xl bg-surface p-1.5 shadow-[var(--shadow-pop)]
        [&>button]:h-10 [&>button]:rounded-xl [&>button]:px-3 [&>button]:text-start [&>button]:font-medium [&>button:hover]:bg-soft"
        (click)="open.set(false)">
        <ng-content />
      </div>
    }
  `,
})
export class RowMenu {
  private readonly host = inject(ElementRef<HTMLElement>);
  readonly label = input('פעולות');
  protected readonly open = signal(false);

  protected outside(e: Event) {
    if (this.open() && !this.host.nativeElement.contains(e.target as Node)) this.open.set(false);
  }
}
