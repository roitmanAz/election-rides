import { Component, ElementRef, OnDestroy, forwardRef, inject, input, signal, viewChild } from '@angular/core';
import { ControlValueAccessor, NG_VALUE_ACCESSOR } from '@angular/forms';
import { Icon } from './icon';

export interface SelectOption {
  value: unknown;
  label: string;
}

/** Options whose value is their own text (reasons lists and the like). */
export const textOptions = (labels: readonly string[]): SelectOption[] => labels.map((l) => ({ value: l, label: l }));
const dmy = (d: string) => d.split('-').reverse().join('.');
export { dmy as dayText };

/**
 * Dropdown in the system's own look, instead of the browser's <select> (whose open list cannot be styled).
 * Works with ngModel. The list floats above the page next to the field, so cards and dialogs never cut it,
 * and opens upward near the bottom of the screen. Keyboard: Enter, Space or arrows open; arrows move;
 * Enter picks; Esc or Tab closes. Closes on a click outside and when anything scrolls.
 */
@Component({
  selector: 'app-select',
  imports: [Icon],
  providers: [{ provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => Select), multi: true }],
  host: { class: 'block', '(document:click)': 'outside($event)' },
  template: `
    <button #btn type="button" [id]="inputId()" class="input flex items-center justify-between gap-2 text-start" [class.invalid]="invalid()"
      [disabled]="disabled()" role="combobox" aria-haspopup="listbox" [attr.aria-expanded]="open()" [attr.aria-label]="ariaLabel() || null"
      (click)="toggle()" (keydown)="key($event)" (blur)="touched()">
      <span class="truncate" [class.text-[#6b766f]]="current() === undefined">{{ current()?.label ?? placeholder() }}</span>
      <app-icon name="chevron" [size]="18" class="text-muted transition-transform" [class.rotate-180]="open()" />
    </button>
    @if (open()) {
      <ul role="listbox" class="fixed z-[70] max-h-72 overflow-y-auto rounded-2xl bg-surface p-1.5 shadow-[var(--shadow-pop)] ring-1 ring-line"
        [style.left.px]="pos().left" [style.width.px]="pos().width" [style.top.px]="pos().top" [style.bottom.px]="pos().bottom">
        @for (o of options(); track $index; let i = $index) {
          <li role="option" [attr.aria-selected]="o.value === value()"
            class="flex cursor-pointer items-center justify-between gap-2 rounded-xl px-3 py-2"
            [class.bg-soft2]="i === active()" [class.font-semibold]="o.value === value()"
            (mousedown)="$event.preventDefault(); pick(o)" (mouseenter)="active.set(i)">
            {{ o.label }}
            @if (o.value === value()) { <app-icon name="check" [size]="16" class="text-action" /> }
          </li>
        }
      </ul>
    }
  `,
})
export class Select implements ControlValueAccessor, OnDestroy {
  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly btn = viewChild.required<ElementRef<HTMLButtonElement>>('btn');
  readonly options = input.required<SelectOption[]>();
  readonly placeholder = input('בחר');
  readonly invalid = input(false);
  readonly inputId = input<string | null>(null);
  readonly ariaLabel = input('');

  protected readonly value = signal<unknown>(undefined);
  protected readonly disabled = signal(false);
  protected readonly open = signal(false);
  protected readonly active = signal(0);
  protected readonly pos = signal<{ left: number; width: number; top: number | null; bottom: number | null }>({ left: 0, width: 0, top: 0, bottom: null });
  private onChange: (v: unknown) => void = () => {};
  private onTouched: () => void = () => {};
  private readonly closeOnScroll = (e: Event) => {
    if (!(e.target instanceof Node && this.host.nativeElement.contains(e.target))) this.close();
  };

  protected current() {
    return this.options().find((o) => o.value === this.value());
  }

  writeValue(v: unknown) {
    this.value.set(v);
  }
  registerOnChange(fn: (v: unknown) => void) {
    this.onChange = fn;
  }
  registerOnTouched(fn: () => void) {
    this.onTouched = fn;
  }
  setDisabledState(d: boolean) {
    this.disabled.set(d);
  }

  protected toggle() {
    if (this.open()) return this.close();
    const r = this.btn().nativeElement.getBoundingClientRect();
    const below = window.innerHeight - r.bottom;
    this.pos.set(below > 220 || below > r.top
      ? { left: r.left, width: r.width, top: r.bottom + 4, bottom: null }
      : { left: r.left, width: r.width, top: null, bottom: window.innerHeight - r.top + 4 });
    this.active.set(Math.max(0, this.options().findIndex((o) => o.value === this.value())));
    this.open.set(true);
    window.addEventListener('scroll', this.closeOnScroll, true);
    window.addEventListener('resize', this.closeOnScroll);
  }

  protected close() {
    this.open.set(false);
    window.removeEventListener('scroll', this.closeOnScroll, true);
    window.removeEventListener('resize', this.closeOnScroll);
  }

  protected pick(o: SelectOption) {
    this.value.set(o.value);
    this.onChange(o.value);
    this.close();
    this.btn().nativeElement.focus();
  }

  protected key(e: KeyboardEvent) {
    const n = this.options().length;
    if (!this.open()) {
      if (['Enter', ' ', 'ArrowDown', 'ArrowUp'].includes(e.key)) {
        e.preventDefault();
        this.toggle();
      }
      return;
    }
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      if (n) this.active.set((this.active() + (e.key === 'ArrowDown' ? 1 : -1) + n) % n);
    } else if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      const o = this.options()[this.active()];
      if (o) this.pick(o);
    } else if (e.key === 'Escape') {
      e.preventDefault();
      e.stopPropagation(); // close the list, not the dialog around it
      this.close();
    } else if (e.key === 'Tab') {
      this.close();
    }
  }

  protected touched() {
    if (!this.open()) this.onTouched();
  }

  protected outside(e: Event) {
    if (this.open() && !this.host.nativeElement.contains(e.target as Node)) this.close();
  }

  ngOnDestroy() {
    this.close();
  }
}
