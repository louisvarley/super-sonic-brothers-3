FROM mcr.microsoft.com/dotnet/sdk:10.0 AS browser-build
WORKDIR /source
COPY src/S2Port.Extraction/ src/S2Port.Extraction/
COPY src/SonicBros3/ src/SonicBros3/
COPY src/SonicBros3.Browser/ src/SonicBros3.Browser/
RUN dotnet publish src/SonicBros3.Browser/SonicBros3.Browser.csproj -c Release -o /browser
COPY tools/ConvertSmb3/ tools/ConvertSmb3/
RUN dotnet publish tools/ConvertSmb3/ConvertSmb3.csproj -c Release -o /converter

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS campaign-converter
WORKDIR /app
COPY --from=browser-build /converter ./
ENTRYPOINT ["dotnet", "ConvertSmb3.dll"]

FROM node:24-alpine
ENV NODE_ENV=production HOST=0.0.0.0 PORT=3000 ROM_DIR=/roms
WORKDIR /app
COPY --chown=node:node package.json ./
COPY --chown=node:node web ./web
COPY --from=browser-build --chown=node:node /browser ./web/runtime
USER node
EXPOSE 3000
HEALTHCHECK --interval=30s --timeout=3s --start-period=20s \
  CMD node -e "fetch('http://127.0.0.1:3000/healthz').then(r=>{if(!r.ok)process.exit(1)}).catch(()=>process.exit(1))"
CMD ["node", "web/server.mjs"]
