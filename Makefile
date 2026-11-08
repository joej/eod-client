# SPDX-License-Identifier: GPL-3.0-or-later
#
# Asset conversion: turns your own DAoC install into the files the client loads, redoing only what is
# out of date. Everything it writes is derived from your install and stays under private/ (git-ignored):
# never commit it or hand it to anyone (see CLAUDE.md, "Game client and assets").
#
#   make list               every zone in your install: number, name, kind, converted or not, check result
#   make zone208            convert zone 208 (Cursed Forest): terrain, objects, water, sky, zone check
#                           (zone8 and zone008 are the same zone; several at once: make zone0 zone208)
#   make check-zone208      ... and fail if its zone check has errors
#   make clean-zone208      delete zone 208's converted files (its models stay in the shared library)
#   make zones              every outdoor zone and capital city, plus private/zones/summary.md
#                           and the figures (make figures: every model id, classic and Catacombs)
#                           and the region loading screens (make loading) and maps (make maps)
#   make check-zones        ... and fail if any zone check has errors
#   make clean-library      delete every converted model and texture (they are redone on demand)
#   make clean              delete everything this Makefile made
#   make -j8 ...            run up to 8 conversions at once (tools/play.sh passes the core count)
#
# Zone numbers are those of src/DaocProtocol/World/zones.tsv (0 = Camelot Hills); files use three digits.
# The install comes from CLIENT=<dir> or tools/play.sh's saved settings (private/play.conf).
#
# How "out of date" is decided. Each output lists the inputs it is made from: the install's files
# (through the asset index), and the converter source files whose code produces it. Models depend on
# the NIF reader and the library; zone files (terrain, placements, water, sky, the check) on the zone
# code and the command line tool. So a change to the terrain code redoes terrain and leaves the 1,000+
# models alone; a NIF reader change reconverts the models and the zones' placements that use them.
# Steps that write several files, or none (a model the reader doesn't take), have a small result file
# as their target: models/.made/<name>.nif holds that model's outcome. Every step writes its outputs
# completely or not at all, so an interrupted run never leaves a file that looks up to date.

SHELL := /bin/bash
.DELETE_ON_ERROR:
.SUFFIXES:
.DEFAULT_GOAL := help

PRIV    := private
BUILD   := $(PRIV)/build
LIB     := $(PRIV)/library
MADE    := $(LIB)/models/.made
INDEX   := $(PRIV)/asset-index/index.json
ZONEDIR := $(PRIV)/zones
TSV     := src/DaocProtocol/World/zones.tsv
KNOWN   := tools/zonecheck-known.txt
MPK     := tools/AssetExtract/bin/Debug/net10.0/mpk
CLIENT  ?= $(shell . $(PRIV)/play.conf 2>/dev/null; echo "$$CLIENT")

# --- what each output is made from -----------------------------------------------------------------
A          := src/DaocAssets
COMMON_SRC := $(wildcard $(A)/Mpk/*.cs $(A)/Library/*.cs) $(A)/TextureResolver.cs $(A)/DaocAssets.csproj
MODEL_SRC  := $(COMMON_SRC) $(wildcard $(A)/Nif/*.cs)
# The command line tool's shared part (entry, option parsing); each output adds its commands' file.
X          := tools/AssetExtract
CLI_SRC    := $(X)/Program.cs $(X)/Options.cs $(X)/GlobalUsings.cs $(X)/AssetExtract.csproj
# Zone files: the zone code (all of it but the world audit, which writes nothing here), the glTF
# writer the terrain and water meshes go through, and the zone and sky commands, which hold the glue.
ZONE_SRC   := $(COMMON_SRC) $(filter-out $(A)/Zone/ZoneAudit.cs,$(wildcard $(A)/Zone/*.cs)) \
              $(A)/Nif/GltfExporter.cs $(A)/Nif/NifMesh.cs $(A)/Nif/NifReader.cs \
              $(CLI_SRC) $(X)/Cli.Zone.cs $(X)/Cli.Sky.cs \
              $(wildcard $(CLIENT)/zones/trees/tree_clusters.mpk)
TOOL_SRC   := $(shell find $(A) tools/AssetExtract -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*') \
              $(A)/DaocAssets.csproj tools/AssetExtract/AssetExtract.csproj

# --- which goals need the install --------------------------------------------------------------------
NO_INSTALL := help clean clean-library $(filter clean-zone%,$(MAKECMDGOALS))
NEEDS_INSTALL := $(filter-out $(NO_INSTALL),$(MAKECMDGOALS))

ifneq ($(NEEDS_INSTALL),)
ifeq ($(wildcard $(CLIENT)/zones),)
$(error no DAoC install: pass CLIENT=<dir> (the folder holding game.dll and zones/), or run tools/play.sh --client <dir> once)
endif
# Which install the outputs come from: a different CLIENT (another install, or this one moved)
# redoes the index and the zone table, which hold its paths. Rewritten only when it changes.
CLIENT_FILE := $(BUILD)/client
$(shell mkdir -p $(BUILD); [ "$$(cat $(CLIENT_FILE) 2>/dev/null)" = "$(CLIENT)" ] || echo "$(CLIENT)" > $(CLIENT_FILE))
-include $(BUILD)/zones.mk
# Only the zones asked for get their model lists read (and made, on first use).
ifneq ($(filter zones check-zones,$(MAKECMDGOALS)),)
WANT := $(ZONES) $(CITIES)
else
# zone8, zone08 and zone008 all mean zone 008.
WANT := $(filter $(ZONES) $(CITIES),$(shell printf '%s\n' $(patsubst check-zone%,%,$(patsubst zone%,%,$(NEEDS_INSTALL))) \
          | grep -E '^[0-9]+$$' | while read -r n; do printf '%03d ' $$(expr $$n + 0); done))
endif
-include $(foreach z,$(WANT),$(ZONEDIR)/zone$(z)/models.mk)
endif

# --- the tool and the index --------------------------------------------------------------------------
# The tool is rebuilt when any of its source changes; outputs wait for it (order-only, after the |) but
# are redone only when their own inputs above changed.
TOOL := $(BUILD)/mpk.built
$(TOOL): $(TOOL_SRC)
	@mkdir -p $(@D)
	dotnet build tools/AssetExtract/AssetExtract.csproj -v q --nologo
	@touch $@

# The index (where every model and texture lives in the install) is rewritten only when its content
# changes, so re-indexing an unchanged install doesn't make 1,000 models look out of date.
$(BUILD)/index.done: $(A)/Library/AssetIndex.cs $(CLIENT_FILE) | $(TOOL)
	@mkdir -p $(@D) $(dir $(INDEX))
	$(MPK) index $(CLIENT) --out $(INDEX).tmp
	@if cmp -s $(INDEX).tmp $(INDEX); then rm $(INDEX).tmp; else mv $(INDEX).tmp $(INDEX); fi
	@touch $@
$(INDEX): $(BUILD)/index.done
	@[ -f $@ ] || { rm -f $<; echo "$@ is missing: run make again to rebuild it" >&2; exit 1; }

$(BUILD)/zones.mk: $(TSV) tools/lib/zones-mk.sh $(CLIENT_FILE) | $(TOOL)
	@mkdir -p $(@D)
	@echo "listing the install's zones…"
	@tools/lib/zones-mk.sh $(CLIENT) $(MPK) $(TSV) > $@.tmp && mv $@.tmp $@

# --- models: one target per model, shared by every zone that places it ------------------------------
$(MADE)/%: $(INDEX) $(MODEL_SRC) | $(TOOL)
	@mkdir -p $(@D)
	@r=$$($(MPK) model $* --index $(INDEX) --library $(LIB)) && echo "$$r" > $@ && echo "  model $*: $$r"

# --- zones ------------------------------------------------------------------------------------------
# $(1) = three-digit zone number. zone<N>_DAT/_POS come from zones.mk, zone<N>_MODELS from models.mk.
define ZONE_RULES
$(ZONEDIR)/zone$(1)/models.mk: $$(zone$(1)_DAT) $(INDEX) $(ZONE_SRC) | $(TOOL)
	@mkdir -p $$(@D)
	@$(MPK) zonedeps $$(zone$(1)_DAT) --index $(INDEX) --var zone$(1)_MODELS > $$@.tmp && mv $$@.tmp $$@

$(ZONEDIR)/zone$(1)/terrain.glb: $$(zone$(1)_DAT) $$(zone$(1)_EDGEDAT) $(INDEX) $(ZONE_SRC) | $(TOOL)
	@mkdir -p $$(@D)
	$(MPK) terrain $$(zone$(1)_DAT) $$(zone$(1)_POS) $$(zone$(1)_EDGES) --index $(INDEX) --library $(LIB) --out $$@

$(ZONEDIR)/zone$(1)/zone.csv: $$(zone$(1)_DAT) $$(zone$(1)_CSV) $(INDEX) $(ZONE_SRC) $$(addprefix $(MADE)/,$$(zone$(1)_MODELS)) | $(TOOL)
	@mkdir -p $$(@D)
	$(MPK) zone $$(zone$(1)_DAT) $$(zone$(1)_POS) --index $(INDEX) --library $(LIB) --out $$(@D)

$(ZONEDIR)/zone$(1)/water.done: $$(zone$(1)_DAT) $(INDEX) $(ZONE_SRC) | $(TOOL)
	@mkdir -p $$(@D)
	$(MPK) water $$(zone$(1)_DAT) $$(zone$(1)_POS) --index $(INDEX) --library $(LIB) --out $$(@D)
	@touch $$@

$(ZONEDIR)/zone$(1)/sky.csv: $$(zone$(1)_DAT) $(INDEX) $(ZONE_SRC) | $(TOOL)
	@mkdir -p $$(@D)
	$(MPK) sky $$(zone$(1)_DAT) --index $(INDEX) --library $(LIB) --out $$(@D)

# The report is kept whatever it finds (exit 1 = errors found); check-zone<N> is what fails on them.
$(ZONEDIR)/zone$(1)/zonecheck.md: $(ZONEDIR)/zone$(1)/zone.csv $(KNOWN) $(ZONE_SRC) | $(TOOL)
	@$(MPK) zonecheck $$(zone$(1)_DAT) --zone $$(@D) --library $(LIB) $$(zone$(1)_POS) --known $(KNOWN) --out $$@.tmp >/dev/null; \
	  rc=$$$$?; [ $$$$rc -le 1 ] || exit $$$$rc; mv $$@.tmp $$@
	@echo "zone $(1) $$(zone$(1)_NAME): $$$$(sed -n 3p $$@)"

.PHONY: zone$(1) check-zone$(1)
# A capital city is all models: no terrain, and no water in its SECTOR.DAT.
zone$(1): $(addprefix $(ZONEDIR)/zone$(1)/,$(if $(filter city,$(zone$(1)_KIND)),zone.csv sky.csv zonecheck.md,terrain.glb zone.csv water.done sky.csv zonecheck.md))

ifneq ($(2),$(1))
.PHONY: zone$(2) check-zone$(2)
zone$(2): zone$(1)
check-zone$(2): check-zone$(1)
endif

check-zone$(1): zone$(1)
	@echo "zone $(1) $$(zone$(1)_NAME): $$$$(sed -n 3p $(ZONEDIR)/zone$(1)/zonecheck.md)"
	@grep -q ': 0 error(s)' $(ZONEDIR)/zone$(1)/zonecheck.md || \
	  { echo "zone $(1) $$(zone$(1)_NAME): zone check found errors, see $(ZONEDIR)/zone$(1)/zonecheck.md" >&2; exit 1; }
endef
$(foreach z,$(WANT),$(eval $(call ZONE_RULES,$(z),$(shell expr $(z) + 0))))

# --- figures: every model id as its classic figure (models/model<id>.glb, figures.csv: NPCs) and its ----
# --- Catacombs version (models/cata<id>.glb, catacombs.csv: players) --------------------------------------
FIGURES := $(LIB)/models/.figures.done
.PHONY: figures
figures: $(FIGURES)
$(FIGURES): $(INDEX) $(MODEL_SRC) $(wildcard $(A)/Figures/*.cs) \
            $(CLI_SRC) $(X)/Cli.Figures.cs $(X)/CataConverter.cs | $(TOOL)
	@mkdir -p $(@D)
	$(MPK) figures --gamedata $(CLIENT)/gamedata.mpk --index $(INDEX) --library $(LIB)
	@touch $@

# --- loading screens: the pictures, tips, bar and font the client shows while it loads a region ----------
LOADING := $(LIB)/loading/loading.csv
.PHONY: loading
loading: $(LOADING)
$(LOADING): $(CLIENT)/gamedata.mpk $(A)/Zone/LoadingScreens.cs $(A)/Zone/IniFile.cs $(CLI_SRC) $(X)/Cli.Loading.cs | $(TOOL)
	@mkdir -p $(@D)
	$(MPK) loading $(CLIENT) --out $(@D)

# --- maps: the zone and region map pictures and their layout (ui/maps), for the map window (M) -------------
MAPS := $(LIB)/maps/.maps.done
.PHONY: maps
maps: $(MAPS)
$(MAPS): $(CLIENT)/ui/maps/regions.dat $(A)/Zone/MapLayout.cs $(A)/Zone/IniFile.cs $(CLI_SRC) $(X)/Cli.Maps.cs $(X)/Cli.Loading.cs | $(TOOL)
	@mkdir -p $(@D)
	$(MPK) maps $(CLIENT) --out $(@D)
	@touch $@

# --- every zone ----------------------------------------------------------------------------------------
.PHONY: zones check-zones
zones: $(foreach z,$(ZONES) $(CITIES),zone$(z)) $(ZONEDIR)/summary.md $(FIGURES) $(LOADING) $(MAPS)

$(ZONEDIR)/summary.md: $(foreach z,$(ZONES) $(CITIES),$(ZONEDIR)/zone$(z)/zonecheck.md)
	@{ echo "# Zone check, every converted zone (outdoor and cities)"; echo; \
	   echo "| Zone | Name | Result |"; echo "|---:|---|---|"; \
	   $(foreach z,$(sort $(ZONES) $(CITIES)),echo "| $(z) | $(zone$(z)_NAME) | $$(sed -n 3p $(ZONEDIR)/zone$(z)/zonecheck.md) |";) \
	 } > $@.tmp && mv $@.tmp $@
	@echo "summary -> $@ ($$(grep -c ': [1-9][0-9]* error(s)' $@) zone(s) with errors)"

check-zones: zones
	@! grep -q '^| [0-9].*: [1-9][0-9]* error(s)' $(ZONEDIR)/summary.md || \
	  { grep '^| [0-9].*: [1-9][0-9]* error(s)' $(ZONEDIR)/summary.md >&2; echo "zone checks found errors" >&2; exit 1; }

# --- cleaning ----------------------------------------------------------------------------------------
.PHONY: clean clean-library help list
clean-zone%:
	rm -rf $(ZONEDIR)/zone$$(printf %03d $$(expr $* + 0))

# One line per zone in the install, in zone order: outdoor zones and cities with their state (not
# converted, or the zone check's result), then the kinds we don't convert yet (dungeons: roadmap M11).
list:
	@$(foreach z,$(sort $(ZONES) $(CITIES) $(OTHERS)),r=$$(sed -n 3p $(ZONEDIR)/zone$(z)/zonecheck.md 2>/dev/null); \
	  k="$(zone$(z)_KIND)"; if [ "$$k" = outdoor ]; then s="$${r:-not converted}"; \
	  elif [ "$$k" = city ]; then s="city: $${r:-not converted}"; else s="$$k: not convertible yet"; fi; \
	  printf '%4d  %-30s %s\n' $$(expr $(z) + 0) "$(zone$(z)_NAME)" "$$s";)

clean-library:
	rm -rf $(LIB)/models $(LIB)/textures

clean: clean-library
	rm -rf $(BUILD) $(wildcard $(ZONEDIR)/zone[0-9][0-9][0-9]) $(ZONEDIR)/summary.md

help:
	@sed -n '3,19p' $(firstword $(MAKEFILE_LIST)) | sed 's/^# \{0,1\}//'
