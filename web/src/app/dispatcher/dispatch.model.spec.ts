import { QueueRow, straightEstimate, waitClass, whyNot } from './dispatch.model';

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

describe('straight-line trip estimate', () => {
  it('adds a third for the streets and drives 25 km/h', () => {
    // About 1.1 km apart in Jerusalem: 1.4 km with the streets, 4 minutes.
    const t = straightEstimate([{ lat: 31.7869, lon: 35.2236 }, { lat: 31.7942, lon: 35.2134 }])!;
    expect(t.km).toBeCloseTo(1.6, 0);
    expect(t.minutes).toBeGreaterThanOrEqual(3);
    expect(straightEstimate([{ lat: 31.78, lon: 35.21 }])).toBeNull();
  });
});
