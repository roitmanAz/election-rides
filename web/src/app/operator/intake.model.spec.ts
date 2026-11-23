import { formatPhone, idValid, isFamily, joinNames, phoneError, Passenger } from './intake.model';

describe('operator rules', () => {
  it('checks phone numbers after removing dashes and spaces', () => {
    expect(phoneError('052-712-3456')).toBe('');
    expect(phoneError('03 618 1234')).toBe('');
    expect(phoneError('077-1234567')).toBe('');
    expect(phoneError('05271234')).toContain('10 ספרות');
    expect(phoneError('036181')).toContain('9 ספרות');
    expect(phoneError('0127123456')).toContain('להתחיל');
    expect(phoneError('')).toContain('צריך');
  });

  it('formats a phone for reading out', () => {
    expect(formatPhone('0527123456')).toBe('052-7123456');
    expect(formatPhone('036181234')).toBe('03-6181234');
  });

  it('validates the ID check digit', () => {
    expect(idValid('032144586')).toBe(true);
    expect(idValid('123456782')).toBe(true);
    expect(idValid('123456789')).toBe(false);
    expect(idValid('12a')).toBe(false);
  });

  it('joins names the way the read-back says them', () => {
    expect(joinNames(['רחל'])).toBe('רחל');
    expect(joinNames(['רחל', 'שמואל'])).toBe('רחל ושמואל');
    expect(joinNames(['א', 'ב', 'ג'])).toBe('א, ב וג');
  });

  it('a request with both genders is a family that travels alone', () => {
    const p = (gender: 'Male' | 'Female') => ({ gender }) as Passenger;
    expect(isFamily([p('Male'), p('Female')])).toBe(true);
    expect(isFamily([p('Male'), p('Male')])).toBe(false);
  });
});
