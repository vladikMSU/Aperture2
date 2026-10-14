# Third-party components

Aperture2 uses the following source checkouts as Git submodules. Their exact commits
are recorded by the parent repository; retain the upstream license files.

| Component | Source | License | Use |
| --- | --- | --- | --- |
| Pi Coding Agent | https://github.com/earendil-works/pi | MIT; see `external/pi/LICENSE` | Agent runtime, RPC and MCP client |
| ThepExcelMCP | https://github.com/ThepExcel/ThepExcelMCP | MIT; see `external/thepexcel/LICENSE` | Existing live Excel operations reused by the Aperture MCP backend |

Custom integration code lives outside the submodules, so an upstream update does
not overwrite local adaptations. Transitive dependencies retain their own licenses.
Python dependency versions are captured by the setup lock file; JavaScript versions
are pinned by Pi's upstream lock file. No provider credentials or signing private keys
belong in this repository.
