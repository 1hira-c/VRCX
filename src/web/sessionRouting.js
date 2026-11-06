function browseDestination(redirect) {
    return typeof redirect === 'string' &&
        redirect.startsWith('/') &&
        !redirect.startsWith('//') &&
        !/^\/login(?:[?#]|$)/.test(redirect)
        ? redirect
        : { name: 'feed' };
}

/** Preserve the current view unless confirmed session state requires a route change. */
export function resolveWebSessionRoute({
    ready,
    serverAuthenticated,
    vrchatStatus,
    workerConnection,
    browseAllowed,
    route
}) {
    if (!ready || !serverAuthenticated || ['checking', 'check_failed'].includes(workerConnection)) return null;
    if (workerConnection === 'unavailable') {
        return browseAllowed && route.name === 'login' ? browseDestination(route.query.redirect) : null;
    }
    if (!browseAllowed && route.name !== 'login') {
        return {
            name: 'login',
            query: route.fullPath && route.fullPath !== '/feed' ? { redirect: route.fullPath } : undefined
        };
    }
    if (!browseAllowed || route.name !== 'login') return null;
    if (vrchatStatus !== 'authenticated' && route.query.reauth === '1') return null;
    return browseDestination(route.query.redirect);
}
