/**
 * @fileoverview The favourite star (AD-44 rule 4): Google's Starred, toggled at once
 * @module Contacts/components/common/FavouriteStar
 */

import React from 'react';
import { IconButton, Tooltip } from '@mui/material';
import { Star, StarBorder } from '@mui/icons-material';
import type { Contact } from '../../types/Contact';
import { useAppDispatch } from '../../types/hooks';
import { changeMembership, STARRED } from '../../redux/thunks/labelThunks';
import { getDisplayName } from '../../model/contactView';
import { notify } from './notify';

/** Star colour: amber, readable on the dark and the light theme */
export const STAR_COLOR = '#f5b301';

/** Whether a contact is a favourite */
export const isFavourite = (c: Contact): boolean => (c.labels ?? []).includes(STARRED);

/**
 * The star of one contact.
 * @param props - The contact, the icon size, and whether to show the outline when off
 * @returns Button
 */
const FavouriteStar: React.FC<{
  contact: Contact;
  size?: 'small' | 'medium';
  /** The outline is shown only on hover (dense lists); the filled star always */
  quiet?: boolean;
}> = ({ contact, size = 'small', quiet = false }) => {
  const dispatch = useAppDispatch();
  const on = isFavourite(contact);
  const label = on ? 'Remove from favourites' : 'Add to favourites';
  return (
    <Tooltip title={label}>
      <IconButton
        size={size}
        aria-label={label}
        aria-pressed={on}
        className={on ? undefined : 'favourite-off'}
        // The row's click opens the card or starts editing: the star only toggles
        onClick={async (e) => {
          e.stopPropagation();
          const refused = await changeMembership(dispatch, [contact.resourceName], STARRED, !on);
          if (refused) {
            notify(
              `Could not ${on ? 'remove' : 'add'} ${getDisplayName(contact)} ${on ? 'from' : 'to'} favourites: ${refused}`,
            );
          }
        }}
        onDoubleClick={(e) => e.stopPropagation()}
        sx={{
          p: 0.25,
          color: on ? STAR_COLOR : 'text.disabled',
          opacity: on || !quiet ? 1 : 0,
          '&:focus-visible': { opacity: 1 },
        }}
      >
        {on ? <Star fontSize="small" /> : <StarBorder fontSize="small" />}
      </IconButton>
    </Tooltip>
  );
};

export default FavouriteStar;
