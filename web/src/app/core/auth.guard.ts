import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { HttpInterceptorFn } from '@angular/common/http';
import { catchError, throwError } from 'rxjs';
import { AuthService } from './auth.service';

/** Signed-in users only; anyone else goes to the sign-in page. */
export const authGuard: CanActivateFn = async () => {
  // inject() only works before the first await.
  const auth = inject(AuthService);
  const router = inject(Router);
  if (auth.me() || (await auth.loadMe())) return true;
  return router.parseUrl('/login');
};

/**
 * When a signed-in user's session ends (idle timeout, user removed, city disabled),
 * the next API call answers 401: go to the sign-in page and say why.
 */
export const sessionInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  return next(req).pipe(
    catchError((e) => {
      if (e?.status === 401 && auth.me() && !req.url.startsWith('/api/auth/')) {
        auth.me.set(null);
        router.navigate(['/login'], { queryParams: { expired: 1 } });
      }
      return throwError(() => e);
    }),
  );
};
