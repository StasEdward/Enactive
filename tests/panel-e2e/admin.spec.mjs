import { test, expect } from '@playwright/test';
import { readFile } from 'node:fs/promises';
const root = new URL('../../src/Enactive.Remote.Gateway/wwwroot/admin/', import.meta.url);
const origin = 'https://admin.example.test';
const user = { id: 'a'.repeat(32), displayName: '<img src=x onerror=alert(1)>', status: 'Active', version: 1, sealedBytes: 2048, createdAt: '2026-10-04T00:00:00Z' };

// Browser tests exercise the shipped scripts and controls. HTTP authorization and database
// projections are covered separately by the Gateway tests with a signed OIDC provider.
for (const width of [1280, 390]) {
  test(`directory search, details and logout at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 950 });
    let authenticated = true, disabled = false, version = 1, registrationState = 'Waiting';
    const currentUser = () => ({ ...user, status: disabled ? 'Disabled' : 'Active', version });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.route(origin + '/**', async route => {
      const url = new URL(route.request().url());
      let data;
      if (route.request().method() === 'POST' && url.pathname.endsWith('/access')) {
        const body = route.request().postDataJSON();
        expect(route.request().headers()['x-csrf-token']).toBe('fixture');
        expect(body.expectedVersion).toBe(version);
        version++; disabled = body.action === 'disable';
        return route.fulfill({ json: { action: body.action, withdrawnCommands: 2 } });
      }
      if (url.pathname === '/admin/api/registrations/decision') {
        const body = route.request().postDataJSON();
        expect(body).toEqual({ provider: 'github', subject: '12345', decision: 'approve', expectedVersion: 0 });
        expect(route.request().headers()['x-csrf-token']).toBe('fixture');
        registrationState = 'Approved';
        return route.fulfill({ json: { hasAccount: false } });
      }
      if (url.pathname === '/admin/api/session') data = { authenticated, csrfToken: 'fixture', expiresAt: '2026-10-05T00:00:00Z' };
      else if (url.pathname === '/admin/api/overview') data = { users: 1, disabledUsers: 0, waitingRegistrations: 1 };
      else if (url.pathname === '/admin/api/users') data = { items: url.searchParams.get('search') === 'missing' ? [] : [currentUser()], next: null };
      else if (url.pathname === '/admin/api/users/' + user.id) data = { user: currentUser(), hosts: 2, devices: 1, tasks: 3, runs: 4, lastHostSeenAt: null };
      else if (url.pathname === '/admin/api/registrations') data = { items: [{ provider: 'github', subject: '12345', display: 'New applicant', state: registrationState, version: 0, requestedAt: user.createdAt, decidedAt: null }], next: null };
      else if (url.pathname === '/admin/api/signout') {
        expect(route.request().headers()['x-csrf-token']).toBe('fixture');
        authenticated = false;
        return route.fulfill({ status: 204 });
      } else {
        const file = url.pathname === '/admin/' ? 'index.html' : url.pathname.split('/').pop();
        if (!['index.html', 'admin.js', 'directory.js', 'admin.css'].includes(file)) return route.fulfill({ status: 404 });
        return route.fulfill({ contentType: file.endsWith('.js') ? 'text/javascript' : file.endsWith('.css') ? 'text/css' : 'text/html', body: await readFile(new URL(file, root), 'utf8'),
          headers: { 'Content-Security-Policy': "default-src 'self'; script-src 'self'; style-src 'self'; object-src 'none'; base-uri 'none'" } });
      }
      return route.fulfill({ json: data });
    });
    await page.goto(origin + '/admin/');
    await expect(page.locator('#directory-rows')).toContainText(user.displayName);
    await expect(page.locator('#directory-rows img')).toHaveCount(0);
    await page.getByRole('button', { name: 'View', exact: true }).click();
    await expect(page.locator('#user-fields')).toContainText('Computers');
    await expect(page.locator('#user-fields')).toContainText(user.displayName);
    await page.getByRole('button', { name: 'Disable account', exact: true }).click();
    await expect(page.locator('#access-description')).toContainText('Already running local work may continue');
    await page.getByRole('button', { name: 'Confirm', exact: true }).click();
    await expect(page.locator('#access-status')).toContainText('Account disabled');
    await expect(page.locator('#directory-rows')).toContainText('Disabled');
    await page.getByRole('button', { name: 'View', exact: true }).click();
    await page.getByRole('button', { name: 'Enable account', exact: true }).click();
    await page.getByRole('button', { name: 'Confirm', exact: true }).click();
    await expect(page.locator('#directory-rows')).toContainText('Active');

    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    if (process.env.ADMIN_SCREENSHOT_DIR) await page.screenshot({ path: `${process.env.ADMIN_SCREENSHOT_DIR}/admin-${width}.png`, fullPage: true });
    await page.getByLabel('Search', { exact: true }).fill('missing');
    await page.getByRole('button', { name: 'Search / refresh' }).click();
    await expect(page.locator('#directory-status')).toHaveText('No matching records.');
    await expect(page.locator('#user-details')).toBeHidden();
    await page.getByLabel('Search', { exact: true }).fill('');
    await page.getByRole('combobox', { name: 'View', exact: true }).selectOption('registrations');
    await expect(page.locator('#directory-state')).toHaveValue('Waiting');
    await expect(page.locator('#directory-rows')).toContainText('New applicant');
    await page.getByRole('button', { name: 'Approve', exact: true }).click();
    await expect(page.locator('#access-description')).toContainText('github:12345');
    await page.getByRole('button', { name: 'Confirm', exact: true }).click();
    await expect(page.locator('#access-status')).toHaveText('Change saved.');

    await page.getByRole('button', { name: 'Sign out', exact: true }).click();
    await expect(page.locator('#session')).toBeHidden();
    await expect(page.locator('#directory-rows')).toBeEmpty();
    expect(errors).toEqual([]);
  });
}
