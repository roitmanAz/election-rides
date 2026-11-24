import { Component, model } from '@angular/core';
import { Icon } from './icon';

/**
 * Eye button that shows or hides a password. Put it inside a relative wrapper next to the input,
 * and bind the input's type: <input [type]="eye.shown() ? 'text' : 'password'" class="input pl-12"> <app-eye #eye />
 * Passwords are typed left to right, so the button sits on the input's left side, away from the text start.
 */
@Component({
  selector: 'app-eye',
  imports: [Icon],
  host: { class: 'absolute inset-y-0 left-1 flex items-center' },
  template: `
    <button type="button" class="grid size-10 place-items-center rounded-lg text-muted hover:bg-soft hover:text-ink"
      [attr.aria-label]="shown() ? 'הסתרת הסיסמה' : 'הצגת הסיסמה'" [attr.aria-pressed]="shown()" (click)="shown.set(!shown())">
      <app-icon [name]="shown() ? 'eyeOff' : 'eye'" [size]="20" />
    </button>
  `,
})
export class Eye {
  readonly shown = model(false);
}
