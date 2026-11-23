import { QueueRow, waitClass, whyNot } from './dispatch.model';

const row = (over: Partial<QueueRow>): QueueRow =>
  ({ id: Math.random().toString(), kind: 'Men', passengers: [{ fullName: 'א', gender: 'Male', clusterId: 'c', ballotBox: null }],
     needsWheelchair: false, waitMinutes: 5, deadlineSoon: false, clusterIds: ['c'], ...over }) as QueueRow;

describe('dispatcher rules', () => {
  it('colors the wait: up to 15 green, 15 to 30 amber, over 30 or a near deadline red', () => {
    expect(waitClass(row({ waitMinutes: 10 }))).toBe('ok');
    expect(waitClass(row({ waitMinutes: 20 }))).toBe('warn');
    expect(waitClass(row({ waitMinutes: 31 }))).toBe('bad');
    expect(waitClass(row({ waitMinutes: 2, deadlineSoon: true }))).toBe('bad');
  });

  it('explains in short words why a request cannot join the car', () => {
    const car = { seats: 2, wheelchairCar: false };
    const man = row({});
    expect(whyNot(car, [man], row({ kind: 'Women' }))).toBe('ברכב גברים');
    expect(whyNot(car, [man], row({ kind: 'Family' }))).toBe('משפחה נוסעת לבד');
    expect(whyNot(car, [], row({ needsWheelchair: true }))).toBe('הרכב לא נגיש');
    expect(whyNot(car, [man, row({})], row({}))).toBe('אין מקום ברכב');
    expect(whyNot(car, [man], row({}))).toBe('');
  });
});
