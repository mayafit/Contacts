import React from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import type { Contact } from '../../../types/Contact';
import FavouriteStar from '../FavouriteStar';
import { changeMembership } from '../../../redux/thunks/labelThunks';
import { onNotify } from '../notify';

jest.mock('../../../types/hooks', () => ({ useAppDispatch: () => jest.fn() }));
jest.mock('../../../redux/thunks/labelThunks', () => ({
  STARRED: 'contactGroups/starred',
  changeMembership: jest.fn(),
}));
const change = changeMembership as jest.MockedFunction<typeof changeMembership>;

const dana = (labels: string[]): Contact =>
  ({ resourceName: 'people/1', names: [{ displayName: 'Dana Cohen' }], labels }) as Contact;

describe('the favourite star (AD-44 rule 4)', () => {
  beforeEach(() => change.mockReset());

  it('adds a contact to Google’s Starred, without the row’s click', async () => {
    change.mockResolvedValue(null);
    const rowClick = jest.fn();
    render(
      <div onClick={rowClick}>
        <FavouriteStar contact={dana([])} />
      </div>,
    );
    const star = screen.getByRole('button', { name: 'Add to favourites' });
    expect(star.getAttribute('aria-pressed')).toBe('false');
    fireEvent.click(star);
    await waitFor(() =>
      expect(change).toHaveBeenCalledWith(
        expect.anything(),
        ['people/1'],
        'contactGroups/starred',
        true,
      ),
    );
    expect(rowClick).not.toHaveBeenCalled();
  });

  it('says so when Google refuses', async () => {
    change.mockResolvedValue('PERMISSION_DENIED');
    const messages: string[] = [];
    const off = onNotify((m) => messages.push(m));
    render(<FavouriteStar contact={dana(['contactGroups/starred'])} />);
    const star = screen.getByRole('button', { name: 'Remove from favourites' });
    expect(star.getAttribute('aria-pressed')).toBe('true');
    fireEvent.click(star);
    await waitFor(() =>
      expect(messages).toEqual(['Could not remove Dana Cohen from favourites: PERMISSION_DENIED']),
    );
    off();
  });
});
