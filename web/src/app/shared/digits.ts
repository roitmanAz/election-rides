import { Directive, ElementRef, inject, input } from '@angular/core';
import { NgControl } from '@angular/forms';

/**
 * Number fields take digits only, and no more than needed (typing or pasting).
 * appDigits="9" for an ID; appDigits="phone" for a phone number: dashes and spaces may stay,
 * and it takes 9 digits for a landline (02, 03, 04, 08, 09) or 10 for a mobile.
 */
@Directive({
  selector: 'input[appDigits]',
  host: { '(input)': 'clean()', inputmode: 'numeric' },
})
export class Digits {
  readonly appDigits = input.required<string | number>();
  private readonly el = inject(ElementRef<HTMLInputElement>);
  private readonly control = inject(NgControl, { optional: true, self: true });

  protected clean() {
    const input = this.el.nativeElement;
    const before = input.value;
    const value = limitDigits(before, this.appDigits());
    if (value === before) return;
    input.value = value;
    // Keep the form's value the same as what is shown.
    this.control?.viewToModelUpdate(value);
    this.control?.control?.setValue(value, { emitModelToViewChange: false });
  }
}

/** The text with only what the field allows, cut at the right number of digits. */
export function limitDigits(text: string, rule: string | number): string {
  const phone = rule === 'phone';
  const kept = phone ? text.replace(/[^\d\- ]/g, '') : text.replace(/\D/g, '');
  const digits = kept.replace(/\D/g, '');
  const max = phone ? (/^0[23489]/.test(digits) ? 9 : 10) : +rule;
  if (digits.length <= max) return kept;
  let count = 0;
  let out = '';
  for (const ch of kept) {
    if (/\d/.test(ch)) {
      if (count === max) break;
      count++;
    }
    out += ch;
  }
  return out;
}
