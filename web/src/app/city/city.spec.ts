import { TestBed } from '@angular/core/testing';
import { Component, signal } from '@angular/core';
import { boxesText } from './clusters';
import { isValidPhone } from './settings';
import { Combo, ComboItem } from '../shared/combo';

describe('cluster boxes text', () => {
  it('joins runs of numbers into ranges', () => {
    expect(boxesText(['10', '12', '14', '15', '16'])).toBe('10, 12, 14–16');
    expect(boxesText(['31', '32', '33', '34', '35'])).toBe('31–35');
    expect(boxesText(['12.1'])).toBe('12.1');
  });
});

describe('phone rules', () => {
  it('accepts mobile and landline numbers and refuses wrong lengths', () => {
    expect(isValidPhone('052-7123456')).toBe(true);
    expect(isValidPhone('02-6543210')).toBe(true);
    expect(isValidPhone('05271234')).toBe(false);
    expect(isValidPhone('016543210')).toBe(false);
  });
});

@Component({
  imports: [Combo],
  template: `<app-combo inputId="c" [items]="items" [(value)]="value" />`,
})
class Host {
  items: ComboItem[] = [
    { id: '1', label: 'רבי עקיבא' },
    { id: '2', label: 'חזון איש' },
  ];
  value = signal<string | null>(null);
}

describe('pick-from-list field', () => {
  it('filters by any part of the name and keeps only a picked item', async () => {
    const f = TestBed.createComponent(Host);
    f.detectChanges();
    const input = f.nativeElement.querySelector('#c') as HTMLInputElement;
    input.value = 'איש';
    input.dispatchEvent(new Event('input'));
    f.detectChanges();
    const options = f.nativeElement.querySelectorAll('[role=option]');
    expect(options.length).toBe(1);
    options[0].dispatchEvent(new Event('mousedown'));
    f.detectChanges();
    expect(f.componentInstance.value()).toBe('2');

    input.value = 'רחוב שלא קיים';
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new Event('blur'));
    f.detectChanges();
    expect(f.componentInstance.value()).toBeNull();
    expect(input.value).toBe('');
  });

  it('shows nothing until something is typed', () => {
    const f = TestBed.createComponent(Host);
    f.detectChanges();
    const input = f.nativeElement.querySelector('#c') as HTMLInputElement;
    input.dispatchEvent(new Event('focus'));
    f.detectChanges();
    expect(f.nativeElement.querySelector('[role=listbox]')).toBeNull();
    input.value = 'ע';
    input.dispatchEvent(new Event('input'));
    f.detectChanges();
    expect(f.nativeElement.querySelector('[role=listbox]')).not.toBeNull();
  });
});
