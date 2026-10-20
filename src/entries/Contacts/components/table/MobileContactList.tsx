/**
 * @fileoverview Contact list for phone screens: one row per person with quick actions
 * @module Contacts/components/table/MobileContactList
 */

import React, { useRef } from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';
import { Box, ButtonBase, Typography } from '@mui/material';
import type { Contact } from '../../types/Contact';
import {
  compareByName,
  getDisplayName,
  getOrganization,
  getPhoneticName,
} from '../../model/contactView';
import { formatRelativeShort, type ActivityIndex } from '../../model/activity';
import ContactAvatar from '../common/ContactAvatar';
import ContactActions from '../device/ContactActions';
import { tokens } from '../../theme/contactsTheme';

/** Props for MobileContactList */
export interface MobileContactListProps {
  contacts: Contact[];
  onOpenCard: (resourceName: string) => void;
  activity?: ActivityIndex;
}

const ROW = 68;

/**
 * Virtualized list: name, phonetic name and organization, with call and
 * WhatsApp buttons; tap a row for the full card.
 * @param props - Component props
 * @returns List element
 */
const MobileContactList: React.FC<MobileContactListProps> = ({
  contacts,
  onOpenCard,
  activity,
}) => {
  const scrollRef = useRef<HTMLDivElement>(null);
  const sorted = React.useMemo(() => [...contacts].sort(compareByName), [contacts]);
  const virtualizer = useVirtualizer({
    count: sorted.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => ROW,
    overscan: 8,
  });

  return (
    <Box ref={scrollRef} sx={{ flex: 1, overflowY: 'auto' }} role="list" aria-label="Contacts">
      <Box sx={{ height: virtualizer.getTotalSize(), position: 'relative' }}>
        {virtualizer.getVirtualItems().map((item) => {
          const c = sorted[item.index];
          const phonetic = getPhoneticName(c);
          const org = getOrganization(c);
          const last = formatRelativeShort(activity?.get(c.resourceName)?.lastInTouch);
          return (
            <Box
              key={c.resourceName}
              role="listitem"
              sx={{
                position: 'absolute',
                top: 0,
                left: 0,
                right: 0,
                height: ROW,
                transform: `translateY(${item.start}px)`,
                display: 'flex',
                alignItems: 'center',
                borderBottom: `1px solid ${tokens.border}`,
              }}
            >
              <ButtonBase
                onClick={() => onOpenCard(c.resourceName)}
                sx={{
                  flex: 1,
                  minWidth: 0,
                  height: '100%',
                  justifyContent: 'flex-start',
                  gap: 1.5,
                  px: 1.5,
                  textAlign: 'left',
                }}
              >
                <ContactAvatar contact={c} size={40} />
                <Box sx={{ minWidth: 0 }}>
                  <Typography
                    variant="body1"
                    noWrap
                    dir="auto"
                    sx={{ fontWeight: 600, textAlign: 'left' }}
                  >
                    {getDisplayName(c)}
                  </Typography>
                  <Typography
                    variant="caption"
                    noWrap
                    dir="auto"
                    sx={{ color: tokens.textMuted, display: 'block', textAlign: 'left' }}
                  >
                    {[phonetic, org, last && `last ${last}`].filter(Boolean).join(' · ') || ' '}
                  </Typography>
                </Box>
              </ButtonBase>
              <Box sx={{ display: 'flex', pr: 0.5 }}>
                <ContactActions contact={c} channels={['call', 'whatsapp']} size="medium" />
              </Box>
            </Box>
          );
        })}
      </Box>
    </Box>
  );
};

export default MobileContactList;
