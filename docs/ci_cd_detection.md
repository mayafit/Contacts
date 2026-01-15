# CI/CD Detection

- Detected: `.github/workflows/` not present in repo root
- No `.gitlab-ci.yml`, `Jenkinsfile`, or `azure-pipelines.yml` found

**Suggestion:** Add a GitHub Actions workflow for test/build steps under `.github/workflows/ci.yml`. I can scaffold one if you'd like.