/**
 * @fileoverview Insights: favourites gone quiet and people who behave like favourites
 * (AD-44 rule 6)
 * @module Contacts/components/insights/FavouritesCard
 */

import React, { useMemo, useState } from 'react';
import { Box, Button, ButtonBase, Paper, Typography } from '@mui/material';
import { Star } from '@mui/icons-material';
import type { Contact } from '../../types/Contact';
import type { ActivityIndex } from '../../model/activity';
import { formatRelative } from '../../model/activity';
import { getDisplayName } from '../../model/contactView';
import { goneQuiet, suggestedFavourites } from '../../model/favourites';
import { useAppDispatch } from '../../types/hooks';
import { changeMembership, STARRED } from '../../redux/thunks/labelThunks';
import ContactAvatar from '../common/ContactAvatar';
import { isFavourite, STAR_COLOR } from '../common/FavouriteStar';
import { notify } from '../common/notify';
import { tokens } from '../../theme/contactsTheme';

const DAY = 86_400_000;

/** One person in a list */
const Row: React.FC<{
  contact: Contact;
  detail: string;
  onOpen: () => void;
  action?: React.ReactNode;
}> = ({ contact, detail, onOpen, action }) => (
  <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, py: 0.5 }}>
    <ButtonBase
      onClick={onOpen}
      sx={{ flex: 1, minWidth: 0, justifyContent: 'flex-start', gap: 1, borderRadius: 1 }}
    >
      <ContactAvatar contact={contact} size={28} />
      <Box sx={{ minWidth: 0, textAlign: 'left' }}>
        <Typography variant="body2" noWrap dir="auto" sx={{ fontWeight: 600 }}>
          {getDisplayName(contact)}
        </Typography>
        <Typography variant="caption" noWrap sx={{ color: tokens.textMuted, display: 'block' }}>
          {detail}
        </Typography>
      </Box>
    </ButtonBase>
    {action}
  </Box>
);

/**
 * The Favourites card.
 * @param props - Contacts, activity (null without T1), open a card
 * @returns Card
 */
const FavouritesCard: React.FC<{
  contacts: Contact[];
  activity: ActivityIndex | null;
  onOpenCard: (resourceName: string) => void;
  onShowFavourites: () => void;
}> = ({ contacts, activity, onOpenCard, onShowFavourites }) => {
  const dispatch = useAppDispatch();
  const [adding, setAdding] = useState<string | null>(null);
  const favourites = useMemo(() => contacts.filter(isFavourite), [contacts]);
  const now = new Date();
  const quiet = useMemo(
    () => (activity ? goneQuiet(contacts, activity) : []),
    [contacts, activity],
  );
  const suggested = useMemo(
    () => (activity ? suggestedFavourites(contacts, activity) : []),
    [contacts, activity],
  );
  const recent = activity
    ? favourites.filter((c) => {
        const last = activity.get(c.resourceName)?.lastInTouch;
        return !!last && now.getTime() - Date.parse(last) <= 90 * DAY;
      }).length
    : null;

  const add = async (c: Contact) => {
    setAdding(c.resourceName);
    const refused = await changeMembership(dispatch, [c.resourceName], STARRED, true);
    setAdding(null);
    if (refused) {
      notify(`Could not add ${getDisplayName(c)} to favourites: ${refused}`);
    }
  };

  return (
    <Paper variant="outlined" sx={{ p: 2.5, borderColor: tokens.border, bgcolor: tokens.surface }}>
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
        <Star sx={{ color: STAR_COLOR }} />
        <Typography variant="subtitle1" sx={{ flex: 1 }}>
          Favourites
        </Typography>
        {favourites.length > 0 && (
          <Button size="small" onClick={onShowFavourites}>
            Show {favourites.length}
          </Button>
        )}
      </Box>
      <Typography variant="caption" sx={{ color: tokens.textMuted }}>
        {favourites.length
          ? `${favourites.length} favourites, the same as on your phone and in Google Contacts` +
            (recent !== null ? ` · ${recent} in touch in the last 3 months` : '')
          : 'No favourites yet: star a contact in the list, or add the people below'}
      </Typography>
      {!activity && (
        <Typography variant="body2" sx={{ color: tokens.textMuted, mt: 1.5 }}>
          Connect Gmail & Calendar to see favourites gone quiet and people you could add.
        </Typography>
      )}
      {activity && (
        <Box
          sx={{
            display: 'grid',
            gridTemplateColumns: { xs: '1fr', md: '1fr 1fr' },
            gap: 2.5,
            mt: 1.5,
          }}
        >
          <Box>
            <Typography variant="subtitle2" sx={{ color: tokens.textSecondary }}>
              Gone quiet
            </Typography>
            {quiet.length === 0 && (
              <Typography variant="body2" sx={{ color: tokens.textMuted }}>
                You are in touch with all your favourites.
              </Typography>
            )}
            {quiet.map((q) => (
              <Row
                key={q.contact.resourceName}
                contact={q.contact}
                detail={
                  q.daysSince === null
                    ? 'No email or meeting in the scan'
                    : `Last in touch ${formatRelative(activity.get(q.contact.resourceName)?.lastInTouch)}`
                }
                onOpen={() => onOpenCard(q.contact.resourceName)}
              />
            ))}
          </Box>
          <Box>
            <Typography variant="subtitle2" sx={{ color: tokens.textSecondary }}>
              People you could add
            </Typography>
            {suggested.length === 0 && (
              <Typography variant="body2" sx={{ color: tokens.textMuted }}>
                Everyone you are often in touch with is a favourite.
              </Typography>
            )}
            {suggested.map((c) => {
              const a = activity.get(c.resourceName);
              return (
                <Row
                  key={c.resourceName}
                  contact={c}
                  detail={`${a?.emails12m ?? 0} emails, ${a?.meetings12m ?? 0} meetings this year · last ${formatRelative(a?.lastInTouch)}`}
                  onOpen={() => onOpenCard(c.resourceName)}
                  action={
                    <Button
                      size="small"
                      startIcon={<Star sx={{ color: STAR_COLOR }} />}
                      disabled={adding === c.resourceName}
                      onClick={() => void add(c)}
                    >
                      Add
                    </Button>
                  }
                />
              );
            })}
          </Box>
        </Box>
      )}
    </Paper>
  );
};

export default FavouritesCard;
