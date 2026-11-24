import { filterRanked, markHtml } from './mark';
import { limitDigits } from './digits';

describe('marking the searched text', () => {
  it('marks every match, ignores letter case, and keeps the text safe', () => {
    expect(markHtml('פתיחת עיר', 'פ')).toContain('>פ</mark>תיחת');
    expect(markHtml('Moshe@Gmail.com', 'gmail')).toContain('>Gmail</mark>');
    expect(markHtml('אבא אבא', 'אבא').match(/<mark/g)?.length).toBe(2);
    expect(markHtml('<b>x</b>', 'x')).not.toContain('<b>');
    expect(markHtml('בלי חיפוש', '')).toBe('בלי חיפוש');
  });
});

describe('order of search results', () => {
  it('puts names that start with the typed text first, then words that start with it, then the rest', () => {
    const names = ['העיר הקדושה', 'רחוב הקדושים', 'קדושת אהרן', 'שמגר'];
    expect(filterRanked(names, 'קדו', (n) => n)).toEqual(['קדושת אהרן', 'העיר הקדושה', 'רחוב הקדושים']);
    expect(filterRanked(names, 'הקד', (n) => n)).toEqual(['העיר הקדושה', 'רחוב הקדושים']);
    expect(filterRanked(['בית ישראל', 'ישראל'], 'ישר', (n) => n)).toEqual(['ישראל', 'בית ישראל']);
  });
});

describe('number fields', () => {
  it('keeps digits only, and no more than needed', () => {
    expect(limitDigits('12a34567890', '9')).toBe('123456789');
    expect(limitDigits('052-712-34567', 'phone')).toBe('052-712-3456');
    expect(limitDigits('02-65432109', 'phone')).toBe('02-6543210');
    expect(limitDigits('12345', '5')).toBe('12345');
  });
});
