fetch('env.config.json')
  .then((response) => response.json())
  .then((data) => {
    window._env = data;
  })
  .catch(() => {
    alert('Failed to fetch environment variables.');
  })
  .finally(() => {
    import('./bootstrap');
  });
