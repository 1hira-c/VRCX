import { beforeEach, describe, expect, it, vi } from 'vitest';

const mocks = vi.hoisted(() => ({
    dispatchRead: vi.fn(),
    watchState: { isLoggedIn: true }
}));

vi.mock('../../api', () => ({ authRequest: {} }));
vi.mock('../../coordinators/userCoordinator', () => ({ getCurrentUser: vi.fn() }));
vi.mock('../appConfig', () => ({
    AppDebug: { endpointDomain: '', endpointDomainVrchat: '', websocketDomain: '', websocketDomainVrchat: '' }
}));
vi.mock('../runtimeCapabilities', () => ({ runtimeCapabilities: { serverMode: false } }));
vi.mock('../syncTelemetry', () => ({ recordAuthenticationRequired: vi.fn() }));
vi.mock('../watchState', () => ({ watchState: mocks.watchState }));
vi.mock('../webapi', () => ({ default: {} }));
vi.mock('../serverReadControl', () => ({
    dispatchServerReadControlRequest: (...args) => mocks.dispatchRead(...args)
}));

import { dispatchControlRequest } from '../serverAuthControl';

describe('serverAuthControl read dispatch', () => {
    beforeEach(() => mocks.dispatchRead.mockReset());

    it('forwards entity-resource through the read-only renderer dispatcher', async () => {
        const stores = { user: { currentUser: { id: 'usr_current' } } };
        const request = {
            operation: 'entity-resource',
            payload: { name: 'userGroups', arguments: { userId: 'usr_target' } }
        };
        const expected = { ok: true, result: [], errorCode: null };
        mocks.dispatchRead.mockResolvedValue(expected);

        await expect(dispatchControlRequest(stores, request)).resolves.toEqual(expected);
        expect(mocks.dispatchRead).toHaveBeenCalledWith(stores, request);
    });
});
