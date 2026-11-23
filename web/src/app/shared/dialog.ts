import { Component, ElementRef, effect, input, output, viewChild } from '@angular/core';
import { Icon } from './icon';

/**
 * Dialog (DESIGN.md): closes by the X, by a click outside and by Esc.
 * On a phone it opens from the bottom as a sheet.
 */
@Component({
  selector: 'app-dialog',
  imports: [Icon],
  template: `
    @if (open()) {
      <div class="fixed inset-0 z-40 flex items-end justify-center bg-[rgba(18,26,20,.35)] sm:items-center sm:p-4"
        (click)="closed.emit()" (keydown.escape)="closed.emit()">
        <div #box role="dialog" aria-modal="true" [attr.aria-label]="title()" tabindex="-1"
          class="max-h-[92vh] w-full overflow-y-auto rounded-t-[var(--radius-frame)] bg-surface shadow-[var(--shadow-pop)] outline-none sm:rounded-[var(--radius-frame)]"
          [style.max-width.px]="width()" (click)="$event.stopPropagation()">
          <div class="sticky top-0 z-10 flex items-center gap-3 border-b border-line bg-surface px-5 py-3">
            <h2 class="text-lg font-semibold">{{ title() }}</h2>
            <button type="button" class="ms-auto grid size-[42px] place-items-center rounded-full bg-soft hover:bg-bad-soft hover:text-bad"
              aria-label="סגירה" (click)="closed.emit()"><app-icon name="x" /></button>
          </div>
          <div class="p-5"><ng-content /></div>
        </div>
      </div>
    }
  `,
})
export class Dialog {
  readonly open = input(false);
  readonly title = input('');
  readonly width = input(560);
  readonly closed = output<void>();
  private readonly box = viewChild<ElementRef<HTMLElement>>('box');

  constructor() {
    // Focus the dialog when it opens, so Esc works right away.
    effect(() => {
      if (this.open()) queueMicrotask(() => this.box()?.nativeElement.focus());
    });
  }
}
