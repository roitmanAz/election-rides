import { markHtml } from './mark';

describe('marking the searched text', () => {
  it('marks every match, ignores letter case, and keeps the text safe', () => {
    expect(markHtml('פתיחת עיר', 'פ')).toContain('>פ</mark>תיחת');
    expect(markHtml('Moshe@Gmail.com', 'gmail')).toContain('>Gmail</mark>');
    expect(markHtml('אבא אבא', 'אבא').match(/<mark/g)?.length).toBe(2);
    expect(markHtml('<b>x</b>', 'x')).not.toContain('<b>');
    expect(markHtml('בלי חיפוש', '')).toBe('בלי חיפוש');
  });
});
