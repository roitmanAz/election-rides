import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, Router } from '@angular/router';
import { LoginPage } from './login';

async function setup() {
  TestBed.configureTestingModule({
    imports: [LoginPage],
    providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
  });
  const fixture = TestBed.createComponent(LoginPage);
  fixture.detectChanges();
  await fixture.whenStable();
  const el = fixture.nativeElement as HTMLElement;
  const http = TestBed.inject(HttpTestingController);
  const type = (id: string, v: string) => {
    const i = el.querySelector<HTMLInputElement>('#' + id)!;
    i.value = v;
    i.dispatchEvent(new Event('input'));
    i.dispatchEvent(new Event('blur'));
    fixture.detectChanges();
  };
  const submit = async () => {
    el.querySelector<HTMLFormElement>('form')!.dispatchEvent(new Event('submit'));
    fixture.detectChanges();
    await fixture.whenStable();
  };
  const settle = async () => {
    await new Promise((r) => setTimeout(r, 0));
    fixture.detectChanges();
    await fixture.whenStable();
  };
  return { fixture, el, http, type, submit, settle };
}

describe('LoginPage', () => {
  it('marks an invalid email when leaving the field', async () => {
    const { el, type, fixture } = await setup();
    type('email', 'not-an-email');
    await fixture.whenStable();
    fixture.detectChanges();
    expect(el.textContent).toContain('האימייל לא תקין');
    expect(el.querySelector('#email')!.classList).toContain('invalid');
  });

  it('asks for the email code, then signs in and goes home', async () => {
    const { el, http, type, submit, settle } = await setup();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    type('email', 'rachel@example.com');
    type('password', 'a-good-password');
    await submit();

    http.expectOne('/api/auth/login').flush({ status: 'code-sent', challengeId: 'c1', email: 'ra***@example.com' });
    await settle();
    expect(el.textContent).toContain('ra***@example.com');

    type('code', '123456');
    await submit();
    const verify = http.expectOne('/api/auth/verify');
    expect(verify.request.body).toEqual({ challengeId: 'c1', code: '123456' });
    verify.flush({ status: 'signed-in', user: { id: 'u1', fullName: 'רחל', email: 'rachel@example.com', role: 'Operator', cityId: 'x', cityName: 'ירושלים', roles: ['Operator'], idleMinutes: 30 } });
    await settle();
    expect(nav).toHaveBeenCalledWith('/');
  });

  it('shows the server message when the password is wrong', async () => {
    const { el, http, type, submit, settle } = await setup();
    type('email', 'rachel@example.com');
    type('password', 'wrong');
    await submit();
    http.expectOne('/api/auth/login').flush({ error: 'האימייל או הסיסמה שגויים' }, { status: 401, statusText: 'Unauthorized' });
    await settle();
    expect(el.textContent).toContain('האימייל או הסיסמה שגויים');
  });
});
