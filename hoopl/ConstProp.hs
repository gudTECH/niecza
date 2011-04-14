{-# OPTIONS_GHC -Wall -fno-warn-name-shadowing #-}
{-# LANGUAGE ScopedTypeVariables, GADTs, NoMonomorphismRestriction #-}
module ConstProp (ConstFact, constLattice, initFact, varHasLit, constProp, constPropPass) where

--import Control.Monad
import Insn
import qualified Data.Map as Map

import Compiler.Hoopl
--import IR
--import OptSupport

-- ConstFact:
--   Not present in map => bottom
--   PElem v => variable has value v
--   Top     => variable's value is not constant
-- Type and definition of the lattice

type ConstFact = Map.Map Int (WithTop Expr)
constLattice :: DataflowLattice ConstFact
constLattice = DataflowLattice
 { fact_name = "Constant propagation"
 , fact_bot  = Map.empty
 , fact_join = joinMaps (extendJoinDomain constFactAdd) }
 where
   constFactAdd _ (OldFact old) (NewFact new) 
       = if new == old then (NoChange, PElem new)
         else               (SomeChange, Top)

-- Initially, we assume that all variable values are unknown.

-- Only interesting semantic choice: values of variables are live across
-- a call site.
-- Note that we don't need a case for x := y, where y holds a constant.
-- We can write the simplest solution and rely on the interleaved optimization.
-- @ start cprop.tex
--------------------------------------------------
-- Analysis: variable equals a literal constant
varHasLit :: FwdTransfer Insn ConstFact
varHasLit = mkFTransfer ft
 where
  ft :: Insn e x -> ConstFact -> Fact x ConstFact

  ft (BifPlus reg _ _)   f = Map.insert reg Top f
  ft (Subcall reg _) f = Map.insert reg Top f
  ft (Fetch reg _) f = Map.insert reg Top f
  ft (RegSet reg constant@(Double _)) f = Map.insert reg (PElem constant) f
  ft (RegSet reg _) f = Map.insert reg Top f

constPropPass = FwdPass
  { fp_lattice  = constLattice
  , fp_transfer = varHasLit
  , fp_rewrite  = constProp }

initFact :: ConstFact
initFact = Map.fromList []
-- @ start cprop.tex
--------------------------------------------------
-- Rewriting: replace constant variables
constProp :: FuelMonad m => FwdRewrite m Insn ConstFact
constProp = mkFRewrite cp
 where
    cp node f = return Nothing
