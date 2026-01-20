import React, { useEffect } from 'react';
import { initContactsEntry, destroyContactsEntry } from './entries/Contacts/contacts.entry';
import ContactsApp from './entries/Contacts/ContactsApp';

import './styles/main.less';

const App = () => {
  useEffect(() => {
    // Initialize Contacts entry with auth reducer
    initContactsEntry();
    return () => {
      destroyContactsEntry();
    };
  }, []);

  return <ContactsApp />;
};

export default App;
