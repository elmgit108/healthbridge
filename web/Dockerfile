# Static demo console — three pages of plain HTML, CSS and JavaScript.
#
# No build step on purpose. React would mean npm, a lockfile and a dist/ stage
# in CI for three pages that call four endpoints. If this ever grows into a real
# application, replace this image; nothing else has to change.
# HEALTHCHECK-friendly binary; the health path is defined in nginx.conf.
# /healthz
FROM nginxinc/nginx-unprivileged:1.27-alpine

# to connect the package to the github repo.
LABEL org.opencontainers.image.source=https://github.com/elmgit108/healthbridge

COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY index.html fhir.html dicom.html style.css /usr/share/nginx/html/

EXPOSE 8081


